using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DackelFxp.App.Converters;
using DackelFxp.Core.Browsing;
using DackelFxp.Core.Tools;

namespace DackelFxp.App.ViewModels;

public sealed partial class FileEntryViewModel(BrowserEntry entry, bool isDrive = false) : ObservableObject
{
    public BrowserEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public bool IsDirectory => Entry.IsDirectory;
    public bool IsDrive { get; } = isDrive;
    public long Size => Entry.IsDirectory && !IsDrive ? -1 : Entry.Size;
    public string SizeText => Entry.IsDirectory && !IsDrive ? "" : Formats.Bytes(Entry.Size);
    public DateTime Modified => Entry.Modified ?? DateTime.MinValue;
    public string ModifiedText => Entry.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string Permissions => Entry.Permissions ?? "";
    public string Owner => string.Join("/", new[] { Entry.Owner, Entry.Group }.Where(s => !string.IsNullOrEmpty(s)));
    /// <summary>Setting "sort links on top" (applies to all panes).</summary>
    public static bool SortLinksOnTop { get; set; }

    public bool IsLink => Entry.IsLink;

    /// <summary>Group for sorting: links (if enabled), then directories, then files.</summary>
    public int SortGroup => SortLinksOnTop && IsLink ? 0 : IsDirectory ? 1 : 2;

    /// <summary>Sort key for the name column, keeping the groups together.</summary>
    public string SortName => SortGroup + Name;

    public string IconKey => IsDrive ? "DriveIcon" : IsDirectory || IsLink ? "FolderIcon" : "FileIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompareOnly), nameof(IsCompareDifferent), nameof(IsCompareSame))]
    public partial CompareStatus? Compare { get; set; }

    // Used as style classes on the name cell (a null brush binding would hide the text).
    public bool IsCompareOnly => Compare is CompareStatus.OnlyLeft or CompareStatus.OnlyRight;
    public bool IsCompareDifferent => Compare is CompareStatus.Different or CompareStatus.Newer or CompareStatus.Older;
    public bool IsCompareSame => Compare is CompareStatus.Same;

    /// <summary>Links: cyan folder; folders: gold; files: blue.</summary>
    public IBrush IconBrush => IsLink ? Brushes.DarkTurquoise : IsDirectory ? Brushes.Goldenrod : Brushes.SteelBlue;
}
