using System.Security.Cryptography;
using System.Text;

namespace ForgeFlow.Security
{
    public enum LicenseAccessMode
    {
        None,
        Licensed,
        Trial
    }

    public sealed class LicenseGuard
    {
        private static readonly byte[] GuardKey = SHA256.HashData(
            Encoding.UTF8.GetBytes("ForgeFlow::Lite::LicenseGuard::2026::" + HardwareIdHelper.GetHardwareId()));

        private readonly LicenseManager _licenseManager;

        public LicenseGuard(LicenseManager licenseManager)
        {
            _licenseManager = licenseManager;
        }

        public GuardDecision EvaluateAccess()
        {
            if (HasUsableLicense())
                return GuardDecision.AllowLicensed(_licenseManager.CurrentLicense.LicenseKey);

            var (allowed, remaining, tampered) = TrialManager.CheckTrial();
            if (tampered)
                return GuardDecision.Deny("Free plan data or system time was tampered with. Upgrade to Pro is required.");

            if (!allowed)
                return GuardDecision.Deny($"The free plan limit is finished. ForgeFlow allows {TrialManager.MaxTrialVideos} successful exports before Pro is required.");

            return GuardDecision.AllowTrial(remaining, TrialManager.GetTrialCount());
        }

        public GuardSession? BeginProcessingSession(out GuardDecision decision)
        {
            decision = EvaluateAccess();
            if (!decision.Allowed)
                return null;

            int trialCountForSession = decision.TrialCount;

            var session = new GuardSession
            {
                Mode = decision.Mode,
                IssuedAtUtc = DateTime.UtcNow,
                Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                MachineToken = HardwareIdHelper.GetLicenseHardwareToken(),
                RemainingAtStart = decision.Remaining,
                TrialCountAtStart = trialCountForSession,
                LicenseKeySnapshot = decision.LicenseKeySnapshot ?? string.Empty,
                LastCheckpoint = "begin",
                CheckpointCount = 0
            };
            session.Token = ComputeSessionToken(session);
            return session;
        }

        public bool AdvanceProcessingSession(GuardSession? session, string checkpoint, out string message)
        {
            message = string.Empty;
            if (session == null)
            {
                message = "License session was not initialized.";
                return false;
            }

            if (session.Completed)
            {
                message = "License session was already completed.";
                return false;
            }

            if (!VerifySessionState(session, out message))
                return false;

            session.CheckpointCount++;
            session.LastCheckpoint = checkpoint;
            session.Token = ComputeSessionToken(session);
            return true;
        }

        public bool CompleteProcessingSession(GuardSession? session, out string message)
        {
            if (!AdvanceProcessingSession(session, "complete", out message))
                return false;

            if (session!.CheckpointCount < 5)
            {
                message = "Processing did not pass all required license checkpoints.";
                return false;
            }

            if (session.Mode == LicenseAccessMode.Trial)
            {
                TrialManager.IncrementTrial();
                if (TrialManager.GetTrialCount() != session.TrialCountAtStart + 1)
                {
                    message = "Free plan export counter failed to advance.";
                    return false;
                }
            }

            session.Completed = true;
            session.LastCheckpoint = "completed";
            session.Token = ComputeSessionToken(session);
            message = string.Empty;
            return true;
        }

        private bool VerifySessionState(GuardSession session, out string message)
        {
            message = string.Empty;
            if (!ConstantTimeEquals(ComputeSessionToken(session), session.Token))
            {
                message = "License session integrity check failed.";
                return false;
            }

            if (session.Mode == LicenseAccessMode.Licensed)
            {
                GuardDecision decision = EvaluateAccess();
                if (!decision.Allowed)
                {
                    message = decision.Message;
                    return false;
                }

                if (decision.Mode != session.Mode)
                {
                    message = "License mode changed during processing.";
                    return false;
                }

                string currentKey = _licenseManager.CurrentLicense.LicenseKey.Trim();
                if (!ConstantTimeEquals(currentKey, session.LicenseKeySnapshot))
                {
                    message = "Active license changed during processing.";
                    return false;
                }
            }
            else if (session.Mode == LicenseAccessMode.Trial)
            {
                var (_, _, tampered) = TrialManager.CheckTrial();
                if (tampered)
                {
                    message = "Free plan data or system time was tampered with during processing.";
                    return false;
                }

                if (TrialManager.GetTrialCount() != session.TrialCountAtStart)
                {
                    message = "Free plan export counter changed during processing.";
                    return false;
                }
            }

            return true;
        }

        private bool HasUsableLicense()
        {
            LicenseInfo license = _licenseManager.CurrentLicense;
            return _licenseManager.IsLicenseValid() &&
                   license.Type != LicenseType.None &&
                   !license.TamperDetected &&
                   license.Status == LicenseStatus.Active;
        }

        private static string ComputeSessionToken(GuardSession session)
        {
            string payload =
                $"{session.Mode}|{session.IssuedAtUtc.Ticks}|{session.Nonce}|{session.MachineToken}|" +
                $"{session.RemainingAtStart}|{session.TrialCountAtStart}|{session.LicenseKeySnapshot}|" +
                $"{session.LastCheckpoint}|{session.CheckpointCount}|{(session.Completed ? 1 : 0)}";

            return Convert.ToHexString(HMACSHA256.HashData(GuardKey, Encoding.UTF8.GetBytes(payload)));
        }

        private static bool ConstantTimeEquals(string left, string right)
        {
            byte[] leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
            byte[] rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
            return leftBytes.Length == rightBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        public sealed class GuardSession
        {
            internal LicenseAccessMode Mode { get; set; }
            internal DateTime IssuedAtUtc { get; set; }
            internal string Nonce { get; set; } = string.Empty;
            internal string MachineToken { get; set; } = string.Empty;
            internal int RemainingAtStart { get; set; }
            internal int TrialCountAtStart { get; set; }
            internal string LicenseKeySnapshot { get; set; } = string.Empty;
            internal string LastCheckpoint { get; set; } = string.Empty;
            internal int CheckpointCount { get; set; }
            internal bool Completed { get; set; }
            internal string Token { get; set; } = string.Empty;
        }
    }

    public sealed class GuardDecision
    {
        public bool Allowed { get; private init; }
        public LicenseAccessMode Mode { get; private init; }
        public int Remaining { get; private init; }
        public int TrialCount { get; private init; }
        public string Message { get; private init; } = string.Empty;
        public string? LicenseKeySnapshot { get; private init; }

        public static GuardDecision AllowLicensed(string licenseKey) => new()
        {
            Allowed = true,
            Mode = LicenseAccessMode.Licensed,
            LicenseKeySnapshot = licenseKey.Trim()
        };

        public static GuardDecision AllowTrial(int remaining, int trialCount) => new()
        {
            Allowed = true,
            Mode = LicenseAccessMode.Trial,
            Remaining = remaining,
            TrialCount = trialCount
        };

        public static GuardDecision Deny(string message) => new()
        {
            Allowed = false,
            Mode = LicenseAccessMode.None,
            Message = message
        };
    }
}
