using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;

namespace Win81Layer;

public sealed class TaskWindow : INotifyPropertyChanged
{
	private string _title = string.Empty;

	private bool _isForeground;

	private bool _showLabel = true;

	private int _count = 1;

	private ImageSource? _icon;

	public string Key { get; init; } = string.Empty;

	public nint Hwnd { get; set; }

	public List<nint> Members { get; } = new List<nint>();

	public ImageSource? Icon
	{
		get
		{
			return _icon;
		}
		set
		{
			if (_icon != value)
			{
				_icon = value;
				Changed("Icon");
			}
		}
	}

	public string Title
	{
		get
		{
			return _title;
		}
		set
		{
			if (_title != value)
			{
				_title = value;
				Changed("Title");
			}
		}
	}

	public bool ShowLabel
	{
		get
		{
			return _showLabel;
		}
		set
		{
			if (_showLabel != value)
			{
				_showLabel = value;
				Changed("ShowLabel");
			}
		}
	}

	public int Count
	{
		get
		{
			return _count;
		}
		set
		{
			if (_count != value)
			{
				_count = value;
				Changed("Count");
				Changed("MultiInstance");
			}
		}
	}

	public bool MultiInstance => _count >= 2;

	public bool IsForeground
	{
		get
		{
			return _isForeground;
		}
		set
		{
			if (_isForeground != value)
			{
				_isForeground = value;
				Changed("IsForeground");
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private void Changed(string p)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
	}
}
