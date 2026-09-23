using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;

namespace Win81Layer;

public sealed class AppEntry : INotifyPropertyChanged
{
	private ImageSource? _icon;

	private Brush _tileBrush = Brushes.Transparent;

	private bool _isOverrideIcon;

	private Brush? _overrideBrush;

	private bool _isSelected;

	public required string Name { get; init; }

	public required string LaunchPath { get; init; }

	public required Brush TileBrush
	{
		get
		{
			return _tileBrush;
		}
		set
		{
			_tileBrush = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TileBrush"));
		}
	}

	public string? AppId { get; set; }

	public DateTime? InstallDate { get; set; }

	public string Category { get; set; } = "";

	public string GroupLetter
	{
		get
		{
			char c = char.ToUpperInvariant(Name.TrimStart().FirstOrDefault());
			return (c >= 'A' && c <= 'Z') ? c.ToString() : "#";
		}
	}

	public string NoGroup => string.Empty;

	public bool IsNew => UsageStore.IsNew(LaunchPath);

	public ImageSource? Icon
	{
		get
		{
			return _icon;
		}
		set
		{
			_icon = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Icon"));
		}
	}

	public bool IsOverrideIcon
	{
		get
		{
			return _isOverrideIcon;
		}
		set
		{
			if (_isOverrideIcon != value)
			{
				_isOverrideIcon = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsOverrideIcon"));
			}
		}
	}

	public Brush? OverrideBrush
	{
		get
		{
			return _overrideBrush;
		}
		set
		{
			_overrideBrush = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("OverrideBrush"));
		}
	}

	public bool IsSelected
	{
		get
		{
			return _isSelected;
		}
		set
		{
			if (_isSelected != value)
			{
				_isSelected = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsSelected"));
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;
}
