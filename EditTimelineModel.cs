using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeFlow
{
    internal sealed class EditTimelineModel
    {
        internal enum ClipKind { Video, Audio, Image, Text, Lens, Blur }

        internal sealed class Clip
        {
            public Guid Id { get; init; } = Guid.NewGuid();
            public string Track { get; set; } = "V1";
            public ClipKind Kind { get; init; }
            public double Start { get; set; }
            public double End { get; set; }
            public double SourceOffset { get; set; }
            public string Source { get; init; } = "";
            public string Text { get; set; } = "";
            public double X { get; set; } = 0.1;
            public double Y { get; set; } = 0.1;
            public double Width { get; set; } = 0.25;
            public double Height { get; set; } = 0.15;
            public double FontSize { get; set; } = 42;
            public string FontFamily { get; set; } = "Arial";
            public string Color { get; set; } = "#FFFFFF";
            public double Volume { get; set; } = 1.0;
            public double Speed { get; set; } = 1.0;
            public bool IsMuted { get; set; }
            public double SourceDuration { get; set; }
            public bool HasVideoTransform { get; set; }
            public double VideoScaleX { get; set; } = 1.0;
            public double VideoScaleY { get; set; } = 1.0;
            public double VideoOffsetX { get; set; }
            public double VideoOffsetY { get; set; }
            public double LensZoom { get; set; } = 4.0;
            public double EffectStrength { get; set; } = 12.0;

            public Clip Copy(bool newIdentity = false) => new()
            {
                Id = newIdentity ? Guid.NewGuid() : Id,
                Track = Track, Kind = Kind, Start = Start, End = End,
                SourceOffset = SourceOffset, Source = Source, Text = Text,
                X = X, Y = Y, Width = Width, Height = Height,
                FontSize = FontSize, FontFamily = FontFamily, Color = Color,
                Volume = Volume, Speed = Speed, IsMuted = IsMuted,
                SourceDuration = SourceDuration,
                HasVideoTransform = HasVideoTransform,
                VideoScaleX = VideoScaleX, VideoScaleY = VideoScaleY,
                VideoOffsetX = VideoOffsetX, VideoOffsetY = VideoOffsetY,
                LensZoom = LensZoom, EffectStrength = EffectStrength
            };
        }

        private sealed record Snapshot(List<Clip> Clips, Guid? SelectedId, double Duration);
        private readonly List<Clip> _clips = new();
        private readonly Stack<Snapshot> _undo = new();
        private readonly Stack<Snapshot> _redo = new();
        private readonly List<Clip> _clipboard = new();

        public double Duration { get; private set; }
        public Guid? SelectedId { get; private set; }
        public IReadOnlyList<Clip> Clips => _clips;
        public Clip? SelectedClip => SelectedId is Guid id ? _clips.FirstOrDefault(c => c.Id == id) : null;

        public void Load(double duration, string source)
        {
            Duration = Math.Max(0.01, duration);
            _clips.Clear(); _undo.Clear(); _redo.Clear(); _clipboard.Clear();
            _clips.Add(new Clip { Track = "V1", Kind = ClipKind.Video, Start = 0, End = Duration, Source = source, SourceDuration = Duration });
            _clips.Add(new Clip { Track = "A1", Kind = ClipKind.Audio, Start = 0, End = Duration, Source = source, SourceDuration = Duration });
            SelectedId = _clips[0].Id;
        }

        public void Select(Guid? id) => SelectedId = id;

        public bool SplitAt(double at, bool linkedVideoAudio = true)
        {
            at = Clamp(at);
            Clip? selected = SelectedClip;
            // Premiere-style keyboard editing: C cuts the clip under the playhead even
            // when the user has just clicked the ruler (which intentionally clears selection).
            if (selected == null || at <= selected.Start + 0.001 || at >= selected.End - 0.001)
            {
                selected = _clips
                    .Where(c => c.Kind == ClipKind.Video && at > c.Start + 0.001 && at < c.End - 0.001)
                    .OrderBy(c => c.Track, StringComparer.Ordinal)
                    .FirstOrDefault()
                    ?? _clips
                        .Where(c => at > c.Start + 0.001 && at < c.End - 0.001)
                        .OrderBy(c => c.Track, StringComparer.Ordinal)
                        .FirstOrDefault();
                SelectedId = selected?.Id;
            }
            if (selected == null) return false;
            SaveUndo();
            var targets = linkedVideoAudio && IsPrimaryMedia(selected)
                ? _clips.Where(c => IsPrimaryMedia(c) && at > c.Start && at < c.End).ToList()
                : _clips.Where(c => c.Id == selected.Id && at > c.Start && at < c.End).ToList();
            if (targets.Count == 0) { _undo.Pop(); return false; }

            Guid? newSelection = null;
            foreach (Clip clip in targets)
            {
                double oldEnd = clip.End;
                double rightOffset = clip.SourceOffset + (at - clip.Start) * (clip.Kind == ClipKind.Audio ? clip.Speed : 1);
                clip.End = at;
                var right = clip.Copy(true);
                right.Start = at; right.End = oldEnd; right.SourceOffset = rightOffset;
                _clips.Add(right);
                if (clip.Id == selected.Id) newSelection = right.Id;
            }
            SelectedId = newSelection ?? selected.Id;
            Sort();
            return true;
        }

        public bool DeleteSelected(bool linkedVideoAudio = true)
        {
            Clip? selected = SelectedClip;
            if (selected == null) return false;
            SaveUndo();
            double deletedStart = selected.Start;
            double deletedEnd = selected.End;
            bool rippleDelete = selected.Track == "V1" || (selected.Track == "A1" && linkedVideoAudio);
            if (linkedVideoAudio && IsPrimaryMedia(selected))
                _clips.RemoveAll(c => IsPrimaryMedia(c) &&
                    Math.Abs(c.Start - selected.Start) < 0.001 && Math.Abs(c.End - selected.End) < 0.001 &&
                    Math.Abs(c.SourceOffset - selected.SourceOffset) < 0.001 &&
                    string.Equals(c.Source, selected.Source, StringComparison.OrdinalIgnoreCase));
            else _clips.Remove(selected);
            if (rippleDelete) RippleCloseGap(deletedStart, deletedEnd);
            SelectedId = null;
            Sort();
            return true;
        }

        public bool CopySelected(bool linkedVideoAudio = true)
        {
            if (SelectedClip is not Clip clip) return false;
            _clipboard.Clear();
            _clipboard.Add(clip.Copy(true));
            if (linkedVideoAudio && IsPrimaryMedia(clip))
            {
                Clip? linked = FindLinkedPrimary(clip);
                if (linked != null) _clipboard.Add(linked.Copy(true));
            }
            return true;
        }

        public bool PasteAt(double at)
        {
            if (_clipboard.Count == 0) return false;
            SaveUndo();
            double clipboardStart = _clipboard.Min(c => c.Start);
            double clipboardEnd = _clipboard.Max(c => c.End);
            double groupLength = clipboardEnd - clipboardStart;
            bool primaryGroup = _clipboard.Any(IsPrimaryMedia);
            double pasteStart = primaryGroup
                ? Math.Clamp(at, 0, Duration)
                : Math.Clamp(at, 0, Math.Max(0, Duration - 0.04));

            if (primaryGroup)
            {
                // Insert edit: preserve the linked picture/sound pair and ripple everything
                // to the right, instead of creating overlapping V1 clips that cannot export.
                foreach (Clip primary in _clips.Where(c => IsPrimaryMedia(c) && pasteStart > c.Start + 0.001 && pasteStart < c.End - 0.001).ToList())
                {
                    double oldEnd = primary.End;
                    Clip right = primary.Copy(true);
                    right.Start = pasteStart;
                    right.End = oldEnd;
                    right.SourceOffset = primary.SourceOffset + (pasteStart - primary.Start) * (primary.Kind == ClipKind.Audio ? primary.Speed : 1);
                    primary.End = pasteStart;
                    _clips.Add(right);
                }
                foreach (Clip existing in _clips.ToList())
                {
                    if (existing.Start >= pasteStart - 0.001)
                    {
                        existing.Start += groupLength;
                        existing.End += groupLength;
                    }
                    else if (!IsPrimaryMedia(existing) && existing.End > pasteStart)
                    {
                        existing.End += groupLength;
                    }
                }
                Duration += groupLength;
            }

            Guid? selected = null;
            foreach (Clip stored in _clipboard)
            {
                Clip pasted = stored.Copy(true);
                double relativeStart = stored.Start - clipboardStart;
                double length = stored.End - stored.Start;
                pasted.Start = pasteStart + relativeStart;
                pasted.End = pasted.Start + length;
                if (!primaryGroup) pasted.End = Math.Min(Duration, pasted.End);
                if (pasted.End <= pasted.Start + 0.001) continue;
                if (pasted.Kind == ClipKind.Image) pasted.Track = NextAvailableTrack("I", 1, pasted.Start, pasted.End);
                else if (pasted.Kind == ClipKind.Text) pasted.Track = NextAvailableTrack("T", 1, pasted.Start, pasted.End);
                else if (pasted.Kind == ClipKind.Lens) pasted.Track = NextAvailableTrack("L", 1, pasted.Start, pasted.End);
                else if (pasted.Kind == ClipKind.Blur) pasted.Track = NextAvailableTrack("E", 1, pasted.Start, pasted.End);
                else if (pasted.Kind == ClipKind.Audio && pasted.Track != "A1") pasted.Track = NextAvailableTrack("A", 2, pasted.Start, pasted.End);
                _clips.Add(pasted);
                if (stored.Kind == ClipKind.Video || selected == null) selected = pasted.Id;
            }
            if (selected == null) { _undo.Pop(); return false; }
            SelectedId = selected;
            Sort();
            return true;
        }

        public bool DuplicateSelected(double at, bool linkedVideoAudio = true)
        {
            if (SelectedClip is not Clip selected) return false;
            if (linkedVideoAudio && IsPrimaryMedia(selected))
            {
                CopySelected(linkedVideoAudio: true);
                return PasteAt(at);
            }
            SaveUndo();
            Clip duplicate = selected.Copy(true);
            double length = duplicate.End - duplicate.Start;
            duplicate.Start = Math.Clamp(at, 0, Math.Max(0, Duration - 0.04));
            duplicate.End = Math.Min(Duration, duplicate.Start + length);
            if (duplicate.End <= duplicate.Start + 0.001) { _undo.Pop(); return false; }
            if (duplicate.Kind == ClipKind.Image) duplicate.Track = NextTrack("I", 1);
            else if (duplicate.Kind == ClipKind.Text) duplicate.Track = NextTrack("T", 1);
            else if (duplicate.Kind == ClipKind.Lens) duplicate.Track = NextTrack("L", 1);
            else if (duplicate.Kind == ClipKind.Blur) duplicate.Track = NextTrack("E", 1);
            else if (duplicate.Kind == ClipKind.Audio && duplicate.Track != "A1") duplicate.Track = NextTrack("A", 2);
            _clips.Add(duplicate);
            SelectedId = duplicate.Id;
            Sort();
            return true;
        }

        public bool RippleTrimToPlayhead(double at, bool trimBefore, bool linkedVideoAudio = true)
        {
            at = Clamp(at);
            Clip? selected = SelectedClip;
            if (selected == null || at <= selected.Start + 0.001 || at >= selected.End - 0.001) return false;

            SaveUndo();
            double removedStart = trimBefore ? selected.Start : at;
            double removedEnd = trimBefore ? at : selected.End;
            double originalStart = selected.Start;
            double originalEnd = selected.End;
            var targets = linkedVideoAudio && IsPrimaryMedia(selected)
                ? _clips.Where(c => IsPrimaryMedia(c) && Math.Abs(c.Start - originalStart) < 0.001 && Math.Abs(c.End - originalEnd) < 0.001).ToList()
                : new List<Clip> { selected };

            foreach (Clip clip in targets)
            {
                if (trimBefore)
                {
                    clip.SourceOffset += (at - clip.Start) * (clip.Kind == ClipKind.Audio ? clip.Speed : 1);
                    clip.Start = at;
                }
                else clip.End = at;
            }

            RippleCloseGap(removedStart, removedEnd, trimBefore ? targets.Select(c => c.Id).ToHashSet() : null);
            Sort();
            return true;
        }

        public bool TrimSelected(double boundary, bool trimStart, bool linkedVideoAudio = true)
        {
            Clip? selected = SelectedClip;
            if (selected == null) return false;
            boundary = Clamp(boundary);
            const double minimumLength = 0.04;
            if (trimStart && (boundary < selected.Start || boundary >= selected.End - minimumLength)) return false;
            if (!trimStart && (boundary > selected.End || boundary <= selected.Start + minimumLength)) return false;
            SaveUndo();
            double originalStart = selected.Start;
            double originalEnd = selected.End;
            var targets = linkedVideoAudio && IsPrimaryMedia(selected)
                ? _clips.Where(c => IsPrimaryMedia(c) &&
                    Math.Abs(c.Start - originalStart) < 0.001 && Math.Abs(c.End - originalEnd) < 0.001).ToList()
                : new List<Clip> { selected };
            foreach (Clip clip in targets)
            {
                if (trimStart)
                {
                    clip.SourceOffset += (boundary - clip.Start) * (clip.Kind == ClipKind.Audio ? clip.Speed : 1);
                    clip.Start = boundary;
                }
                else clip.End = boundary;
            }
            Sort();
            return true;
        }

        public bool MoveSelected(double newStart, bool linkedVideoAudio = true)
        {
            Clip? selected = SelectedClip;
            if (selected == null) return false;
            double originalStart = selected.Start;
            double originalEnd = selected.End;
            double length = originalEnd - originalStart;
            newStart = Math.Clamp(newStart, 0, Math.Max(0, Duration - length));

            bool reorderPrimary = selected.Track == "V1" || (selected.Track == "A1" && linkedVideoAudio);
            if (reorderPrimary)
            {
                List<Clip> ordered = _clips.Where(c => c.Track == selected.Track).OrderBy(c => c.Start).ToList();
                List<Guid> before = ordered.Select(c => c.Id).ToList();
                ordered.Remove(selected);
                int insertion = ordered.Count(c => c.Start + ((c.End - c.Start) / 2.0) < newStart);
                ordered.Insert(insertion, selected);
                if (before.SequenceEqual(ordered.Select(c => c.Id))) return false;

                SaveUndo();
                var originalBounds = ordered.ToDictionary(c => c.Id, c => (c.Start, c.End, c.SourceOffset));
                double cursor = 0;
                foreach (Clip clip in ordered)
                {
                    (double oldStart, double oldEnd, double sourceOffset) = originalBounds[clip.Id];
                    double duration = oldEnd - oldStart;
                    clip.Start = cursor;
                    clip.End = cursor + duration;
                    cursor += duration;
                    if (!linkedVideoAudio || !IsPrimaryMedia(selected)) continue;
                    string linkedTrack = clip.Track == "V1" ? "A1" : "V1";
                    Clip? linked = _clips.FirstOrDefault(c => c.Track == linkedTrack &&
                        Math.Abs(c.Start - oldStart) < 0.001 && Math.Abs(c.End - oldEnd) < 0.001 &&
                        Math.Abs(c.SourceOffset - sourceOffset) < 0.001 &&
                        string.Equals(c.Source, clip.Source, StringComparison.OrdinalIgnoreCase));
                    if (linked != null) { linked.Start = clip.Start; linked.End = clip.End; }
                }
                Sort();
                return true;
            }

            if (Math.Abs(newStart - originalStart) < 0.001) return false;
            SaveUndo();
            selected.Start = newStart;
            selected.End = newStart + length;
            Sort();
            return true;
        }

        public Clip AddImage(string path, double at, double length = 5)
        {
            SaveUndo();
            double start = Clamp(at);
            double end = Math.Min(Duration, start + length);
            var clip = new Clip { Track = NextAvailableTrack("I", 1, start, end), Kind = ClipKind.Image, Source = path, Start = start, End = end };
            _clips.Add(clip); SelectedId = clip.Id; Sort(); return clip;
        }

        public Clip AddText(string text, double at, double length = 5)
        {
            SaveUndo();
            double naturalWidth = Math.Clamp(0.08 + Math.Max(1, text.Length) * 0.022, 0.15, 0.72);
            double start = Clamp(at);
            double end = Math.Min(Duration, start + length);
            var clip = new Clip { Track = NextAvailableTrack("T", 1, start, end), Kind = ClipKind.Text, Text = text, Start = start, End = end, Width = naturalWidth, Height = 0.08 };
            _clips.Add(clip); SelectedId = clip.Id; Sort(); return clip;
        }

        public Clip AddLens(double at, double length = 5)
        {
            SaveUndo();
            double start = Math.Clamp(at, 0, Math.Max(0, Duration - 0.04));
            var clip = new Clip
            {
                Track = NextAvailableTrack("L", 1, start, Math.Min(Duration, start + Math.Max(0.04, length))), Kind = ClipKind.Lens,
                Start = start, End = Math.Min(Duration, start + Math.Max(0.04, length)),
                X = 0.5, Y = 0.5, Width = 0.28, Height = 0.28, LensZoom = 4
            };
            _clips.Add(clip); SelectedId = clip.Id; Sort(); return clip;
        }

        public Clip AddBlur(double at, double length = 5)
        {
            SaveUndo();
            double start = Math.Clamp(at, 0, Math.Max(0, Duration - 0.04));
            var clip = new Clip
            {
                Track = NextAvailableTrack("E", 1, start, Math.Min(Duration, start + Math.Max(0.04, length))), Kind = ClipKind.Blur,
                Start = start, End = Math.Min(Duration, start + Math.Max(0.04, length)),
                EffectStrength = 12
            };
            _clips.Add(clip); SelectedId = clip.Id; Sort(); return clip;
        }

        public Clip AddAudio(string path, double at, double sourceDuration)
        {
            SaveUndo();
            double start = Math.Clamp(at, 0, Math.Max(0, Duration - 0.04));
            double available = Math.Max(0.04, Duration - start);
            double length = Math.Min(Math.Max(0.04, sourceDuration), available);
            var clip = new Clip
            {
                Track = NextAvailableTrack("A", 2, start, start + length), Kind = ClipKind.Audio, Source = path,
                Start = start, End = start + length, SourceDuration = Math.Max(length, sourceDuration)
            };
            _clips.Add(clip); SelectedId = clip.Id; Sort(); return clip;
        }

        public Clip AddVideo(string path, double at, double sourceDuration)
        {
            double length = Math.Max(0.04, sourceDuration);
            at = Math.Clamp(at, 0, Duration);
            SaveUndo();

            foreach (Clip primary in _clips.Where(c => IsPrimaryMedia(c) && at > c.Start + 0.001 && at < c.End - 0.001).ToList())
            {
                double oldEnd = primary.End;
                var right = primary.Copy(true);
                right.Start = at;
                right.End = oldEnd;
                right.SourceOffset = primary.SourceOffset + (at - primary.Start) * (primary.Kind == ClipKind.Audio ? primary.Speed : 1);
                primary.End = at;
                _clips.Add(right);
            }

            foreach (Clip clip in _clips.ToList())
            {
                if (clip.Start >= at - 0.001)
                {
                    clip.Start += length;
                    clip.End += length;
                }
                else if (!IsPrimaryMedia(clip) && clip.End > at)
                {
                    clip.End += length;
                }
            }

            var video = new Clip
            {
                Track = "V1", Kind = ClipKind.Video, Source = path,
                Start = at, End = at + length, SourceDuration = length
            };
            var audio = new Clip
            {
                Track = "A1", Kind = ClipKind.Audio, Source = path,
                Start = at, End = at + length, SourceDuration = length
            };
            _clips.Add(video);
            _clips.Add(audio);
            Duration += length;
            SelectedId = video.Id;
            Sort();
            return video;
        }

        public bool UpdateAudio(Guid id, double volume, double speed, bool muted, bool saveUndo = true)
        {
            Clip? clip = _clips.FirstOrDefault(c => c.Id == id && c.Kind == ClipKind.Audio);
            if (clip == null) return false;
            volume = Math.Clamp(volume, 0, 2);
            speed = Math.Clamp(speed, 0.5, 2);
            if (Math.Abs(clip.Volume - volume) < 0.001 && Math.Abs(clip.Speed - speed) < 0.001 && clip.IsMuted == muted)
                return false;
            if (saveUndo) SaveUndo();
            clip.Volume = volume;
            clip.Speed = speed;
            clip.IsMuted = muted;
            if (clip.Track != "A1" && clip.Track.StartsWith("A", StringComparison.Ordinal) && clip.SourceDuration > 0)
                clip.End = Math.Min(Duration, clip.Start + (clip.SourceDuration - clip.SourceOffset) / clip.Speed);
            Sort();
            return true;
        }

        public bool SetAudioTrackMuted(string track, bool muted)
        {
            List<Clip> clips = _clips.Where(c => c.Kind == ClipKind.Audio && c.Track == track).ToList();
            if (clips.Count == 0 || clips.All(c => c.IsMuted == muted)) return false;
            SaveUndo();
            foreach (Clip clip in clips) clip.IsMuted = muted;
            return true;
        }

        public void BeginUndoGroup() => SaveUndo();

        public bool UpdateLens(Guid id, double x, double y, double size, double zoom)
        {
            Clip? clip = _clips.FirstOrDefault(c => c.Id == id && c.Kind == ClipKind.Lens);
            if (clip == null) return false;
            clip.X = Math.Clamp(x, 0, 1);
            clip.Y = Math.Clamp(y, 0, 1);
            clip.Width = clip.Height = Math.Clamp(size, 0.08, 0.60);
            clip.LensZoom = Math.Clamp(zoom, 2, 6);
            return true;
        }

        public bool UpdateBlur(Guid id, double strength)
        {
            Clip? clip = _clips.FirstOrDefault(c => c.Id == id && c.Kind == ClipKind.Blur);
            if (clip == null) return false;
            clip.EffectStrength = Math.Clamp(strength, 0, 50);
            return true;
        }

        public bool UpdateVideoTransform(Guid id, double scaleX, double scaleY, double offsetX, double offsetY)
        {
            Clip? clip = _clips.FirstOrDefault(c => c.Id == id && c.Kind == ClipKind.Video);
            if (clip == null) return false;
            scaleX = Math.Clamp(scaleX, 1, 4);
            scaleY = Math.Clamp(scaleY, 1, 4);
            offsetX = Math.Clamp(offsetX, -1, 1);
            offsetY = Math.Clamp(offsetY, -1, 1);
            if (clip.HasVideoTransform &&
                Math.Abs(clip.VideoScaleX - scaleX) < 0.001 && Math.Abs(clip.VideoScaleY - scaleY) < 0.001 &&
                Math.Abs(clip.VideoOffsetX - offsetX) < 0.001 && Math.Abs(clip.VideoOffsetY - offsetY) < 0.001)
                return false;
            SaveUndo();
            clip.HasVideoTransform = true;
            clip.VideoScaleX = scaleX;
            clip.VideoScaleY = scaleY;
            clip.VideoOffsetX = offsetX;
            clip.VideoOffsetY = offsetY;
            return true;
        }

        public bool Undo() => Restore(_undo, _redo);
        public bool Redo() => Restore(_redo, _undo);
        public IEnumerable<Clip> ActiveOverlays(double at) => _clips.Where(c =>
            (c.Kind == ClipKind.Image || c.Kind == ClipKind.Text) && at >= c.Start && at < c.End);

        private void SaveUndo() { _undo.Push(Capture()); _redo.Clear(); }
        private Snapshot Capture() => new(_clips.Select(c => c.Copy()).ToList(), SelectedId, Duration);

        private bool Restore(Stack<Snapshot> source, Stack<Snapshot> destination)
        {
            if (source.Count == 0) return false;
            destination.Push(Capture());
            Snapshot snapshot = source.Pop();
            _clips.Clear(); _clips.AddRange(snapshot.Clips.Select(c => c.Copy()));
            SelectedId = snapshot.SelectedId;
            Duration = snapshot.Duration;
            return true;
        }

        private void RippleCloseGap(double start, double end, HashSet<Guid>? preserved = null)
        {
            double gap = Math.Max(0, end - start);
            if (gap <= 0) return;
            foreach (Clip clip in _clips.ToList())
            {
                if (preserved?.Contains(clip.Id) == true)
                {
                    clip.Start = Math.Max(0, clip.Start - gap);
                    clip.End = Math.Max(clip.Start + 0.04, clip.End - gap);
                    continue;
                }
                if (clip.Start >= end - 0.001)
                {
                    clip.Start = Math.Max(0, clip.Start - gap);
                    clip.End = Math.Max(clip.Start + 0.04, clip.End - gap);
                }
                else if (clip.Start < start && clip.End > end)
                {
                    clip.End -= gap;
                }
                else if (clip.Start >= start && clip.End > end)
                {
                    clip.Start = start;
                    clip.End -= gap;
                }
                else if (clip.Start < start && clip.End > start)
                {
                    clip.End = start;
                }
                else if (clip.Start >= start && clip.End <= end)
                {
                    _clips.Remove(clip);
                }
            }
            Duration = Math.Max(0.04, Duration - gap);
        }

        private void Sort() => _clips.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : string.CompareOrdinal(a.Track, b.Track));
        private static bool IsPrimaryMedia(Clip clip) => clip.Track is "V1" or "A1";

        private Clip? FindLinkedPrimary(Clip clip)
        {
            string track = clip.Track == "V1" ? "A1" : "V1";
            return _clips.FirstOrDefault(candidate => candidate.Track == track
                && Math.Abs(candidate.Start - clip.Start) < 0.001
                && Math.Abs(candidate.End - clip.End) < 0.001
                && Math.Abs(candidate.SourceOffset - clip.SourceOffset) < 0.001
                && string.Equals(candidate.Source, clip.Source, StringComparison.OrdinalIgnoreCase));
        }

        private string NextAvailableTrack(string prefix, int first, double start, double end)
        {
            int candidate = first;
            while (_clips.Any(c => c.Track == prefix + candidate && c.Start < end - 0.001 && c.End > start + 0.001))
                candidate++;
            return prefix + candidate;
        }

        private string NextTrack(string prefix, int first)
        {
            HashSet<int> used = _clips
                .Where(c => c.Track.StartsWith(prefix, StringComparison.Ordinal))
                .Select(c => int.TryParse(c.Track[prefix.Length..], out int number) ? number : -1)
                .Where(number => number >= first)
                .ToHashSet();
            int candidate = first;
            while (used.Contains(candidate)) candidate++;
            return prefix + candidate;
        }
        private double Clamp(double value) => Math.Clamp(value, 0, Duration);
    }
}
