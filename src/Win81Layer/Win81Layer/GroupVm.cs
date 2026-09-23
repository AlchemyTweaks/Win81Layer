using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Win81Layer;

public sealed class GroupVm : INotifyPropertyChanged
{
	private string _name = string.Empty;

	private bool _isEditing;

	private bool _showNamePrompt;

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			if (!(_name == value))
			{
				_name = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Name"));
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("NamePromptVisible"));
			}
		}
	}

	public bool IsEditing
	{
		get
		{
			return _isEditing;
		}
		set
		{
			if (_isEditing != value)
			{
				_isEditing = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsEditing"));
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("NamePromptVisible"));
			}
		}
	}

	public bool ShowNamePrompt
	{
		get
		{
			return _showNamePrompt;
		}
		set
		{
			if (_showNamePrompt != value)
			{
				_showNamePrompt = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ShowNamePrompt"));
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("NamePromptVisible"));
			}
		}
	}

	public bool NamePromptVisible => _showNamePrompt && !_isEditing && string.IsNullOrWhiteSpace(_name);

	public ObservableCollection<TileVm> Tiles { get; } = new ObservableCollection<TileVm>();

	// Workspace: the saved on-screen window layout for this group (persisted via Profile; empty = none saved).
	public System.Collections.Generic.List<Profile.WindowLayoutRecord> SavedLayout { get; set; } = new System.Collections.Generic.List<Profile.WindowLayoutRecord>();

	public event PropertyChangedEventHandler? PropertyChanged;
}
