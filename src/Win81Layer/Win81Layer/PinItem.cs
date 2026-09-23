using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;

namespace Win81Layer;

public abstract class PinItem : INotifyPropertyChanged
{
	private ImageSource? _icon;

	private bool _isRunning;

	private bool _isForeground;

	private bool _multiInstance;

	public string Name { get; init; } = "";

	public List<nint> RunningHwnds { get; } = new List<nint>();

	public ImageSource? Icon
	{
		get
		{
			return _icon;
		}
		set
		{
			_icon = value;
			OnCh("Icon");
		}
	}

	public bool IsRunning
	{
		get
		{
			return _isRunning;
		}
		set
		{
			if (_isRunning != value)
			{
				_isRunning = value;
				OnCh("IsRunning");
			}
		}
	}

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
				OnCh("IsForeground");
			}
		}
	}

	public bool MultiInstance
	{
		get
		{
			return _multiInstance;
		}
		set
		{
			if (_multiInstance != value)
			{
				_multiInstance = value;
				OnCh("MultiInstance");
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnCh(string n)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
	}
}
