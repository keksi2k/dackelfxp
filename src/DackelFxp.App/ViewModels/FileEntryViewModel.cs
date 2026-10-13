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
    /// <summary>Sort key keeping directories on top.</summary>
    public string SortName => (IsDirectory ? "0" : "1") + Name;

    public string IconKey => IsDrive ? "DriveIcon" : Entry.IsLink ? "LinkIcon" : IsDirectory ? "FolderIcon" : "FileIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompareOnly), nameof(IsCompareDifferent), nameof(IsCompareSame))]
    public partial CompareStatus? Compare { get; set; }

    // Used as style classes on the name cell (a null brush binding would hide the text).
    public bool IsCompareOnly => Compare is CompareStatus.OnlyLeft or CompareStatus.OnlyRight;
    public bool IsCompareDifferent => Compare is CompareStatus.Different or CompareStatus.Newer or CompareStatus.Older;
    public bool IsCompareSame => Compare is CompareStatus.Same;

    public IBrush IconBrush => IsDirectory ? Brushes.Goldenrod : Brushes.SteelBlue;
}
