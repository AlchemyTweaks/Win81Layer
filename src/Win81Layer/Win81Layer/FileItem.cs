#nullable enable

using System;
using System.ComponentModel;
using System.Windows.Media;

namespace Win81Layer;

internal sealed class FileItem : INotifyPropertyChanged
{
	public bool IconRequested;

	private ImageSource? _icon;

	public string Path { get; init; } = "";

	public string Display { get; init; } = "";

	public bool IsDir { get; init; }

	public bool IsDrive { get; init; }

	public string TypeText { get; init; } = "";

	public string DateModifiedText { get; init; } = "";

	public string SizeText { get; init; } = "";

	public string SecondaryText { get; init; } = "";

	public double UsagePercent { get; init; }

	public bool HasUsageBar { get; init; }

	public string AssetKey { get; init; } = "";

	public ImageSource? Icon
	{
		get
		{
			return _icon;
		}
		set
		{
			if (!ReferenceEquals(_icon, value))
			{
				_icon = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;
}
