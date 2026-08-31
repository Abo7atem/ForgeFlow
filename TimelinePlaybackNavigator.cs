using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeFlow;

/// <summary>
/// Pure timeline-boundary calculations used by the live preview. Keeping these
/// decisions outside MediaElement makes cut transitions deterministic and testable.
/// </summary>
internal static class TimelinePlaybackNavigator
{
    internal static List<EditTimelineModel.Clip> VideoClips(IEnumerable<EditTimelineModel.Clip> clips) => clips
        .Where(clip => clip.Kind == EditTimelineModel.ClipKind.Video)
        .OrderBy(clip => clip.Start)
        .ThenBy(clip => clip.Track, StringComparer.Ordinal)
        .ToList();

    internal static EditTimelineModel.Clip? Current(IEnumerable<EditTimelineModel.Clip> clips, double timelinePosition)
    {
        List<EditTimelineModel.Clip> ordered = VideoClips(clips);
        EditTimelineModel.Clip? exact = ordered.FirstOrDefault(clip =>
            timelinePosition >= clip.Start && timelinePosition < clip.End);
        if (exact != null) return exact;

        // MediaEnded can arrive before the 40 ms UI timer publishes its last tick.
        // Only use tolerance when no exact clip owns the playhead, otherwise the
        // tolerance bands of two adjoining clips overlap and can skip a segment.
        return ordered
            .Where(clip => timelinePosition >= clip.Start - 0.04 && timelinePosition <= clip.End + 0.04)
            .OrderBy(clip => DistanceToRange(timelinePosition, clip.Start, clip.End))
            .ThenBy(clip => clip.Start)
            .FirstOrDefault();
    }

    private static double DistanceToRange(double value, double start, double end) =>
        value < start ? start - value : value > end ? value - end : 0;

    internal static EditTimelineModel.Clip? Next(
        IEnumerable<EditTimelineModel.Clip> clips,
        EditTimelineModel.Clip? current,
        double timelinePosition)
    {
        List<EditTimelineModel.Clip> ordered = VideoClips(clips);
        current ??= Current(ordered, timelinePosition);
        double searchFrom = current?.End ?? timelinePosition;
        return ordered.FirstOrDefault(clip =>
            clip.Id != current?.Id && clip.Start >= searchFrom - 0.003);
    }

    internal static double PlaybackEnd(IEnumerable<EditTimelineModel.Clip> clips) =>
        VideoClips(clips).Select(clip => clip.End).DefaultIfEmpty(0).Max();
}
