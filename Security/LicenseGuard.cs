using System.Security.Cryptography;
using System.Text;

namespace ForgeFlow.Security
{
    public enum LicenseAccessMode
    {
        None,
        Free
    }

    public sealed class LicenseGuard
    {
        private static readonly byte[] GuardKey = SHA256.HashData(
            Encoding.UTF8.GetBytes("ForgeFlow::Free::ProcessingSession::2026::" + HardwareIdHelper.GetHardwareId()));

        public GuardDecision EvaluateAccess() => GuardDecision.AllowFree();

        public GuardSession BeginProcessingSession(out GuardDecision decision)
        {
            decision = EvaluateAccess();
            var session = new GuardSession
            {
                Mode = LicenseAccessMode.Free,
                IssuedAtUtc = DateTime.UtcNow,
                Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                MachineToken = HardwareIdHelper.GetLicenseHardwareToken(),
                LastCheckpoint = "begin"
            };
            session.Token = ComputeSessionToken(session);
            return session;
        }

        public bool AdvanceProcessingSession(GuardSession? session, string checkpoint, out string message)
        {
            message = string.Empty;
            if (session == null)
            {
                message = "Processing session was not initialized.";
                return false;
            }

            if (session.Completed)
            {
                message = "Processing session was already completed.";
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
                message = "Processing did not pass all required checkpoints.";
                return false;
            }

            session.Completed = true;
            session.LastCheckpoint = "completed";
            session.Token = ComputeSessionToken(session);
            message = string.Empty;
            return true;
        }

        private static bool VerifySessionState(GuardSession session, out string message)
        {
            message = string.Empty;
            if (!ConstantTimeEquals(ComputeSessionToken(session), session.Token))
            {
                message = "Processing session integrity check failed.";
                return false;
            }

            if (session.Mode != LicenseAccessMode.Free)
            {
                message = "Processing session mode is not supported.";
                return false;
            }

            return true;
        }

        private static string ComputeSessionToken(GuardSession session)
        {
            string payload =
                $"{session.Mode}|{session.IssuedAtUtc.Ticks}|{session.Nonce}|{session.MachineToken}|" +
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
        public string Message { get; private init; } = string.Empty;

        public static GuardDecision AllowFree() => new()
        {
            Allowed = true,
            Mode = LicenseAccessMode.Free
        };

        public static GuardDecision Deny(string message) => new()
        {
            Allowed = false,
            Mode = LicenseAccessMode.None,
            Message = message
        };
    }
}
