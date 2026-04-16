using Godot;

public partial class ClassSelectUi : Control
{
    [Export] public string BattlefieldScenePath = "res://Scenes/Battlefield.tscn";

    private OptionButton _schoolPicker;
    private Label _descriptionLabel;
    private Button _confirmButton;
	private CheckBox _debugCheckbox;

    // Description for each school shown below the picker
    private static readonly System.Collections.Generic.Dictionary<CardSchool, string> Descriptions = new()
    {
        { CardSchool.Arcanist,    "Masters of raw magic. High damage spells and mana manipulation." },
        { CardSchool.Elementalist,"Controls terrain with fire, ice, and storm effects." },
        { CardSchool.Necromancer, "Summons minions and drains life from enemies." },
        { CardSchool.Enchanter,   "Buffs, debuffs, and tile enchantments." },
        { CardSchool.Tinker,    "Mechanical traps, turrets, and area control." },
        { CardSchool.Generic,     "A mixed deck drawn from all schools." },
    };

    public override void _Ready()
    {

		if (CardDatabase.Blueprints.Count == 0)
    		CardDatabase.LoadFromCsv("res://Data/cards.csv");

        _schoolPicker   = GetNode<OptionButton>("MarginContainer/VBox/SchoolPicker");
        _descriptionLabel = GetNode<Label>("MarginContainer/VBox/DescriptionLabel");
        _confirmButton  = GetNode<Button>("MarginContainer/VBox/ConfirmButton");
		_debugCheckbox    = GetNode<CheckBox>("MarginContainer/VBox/DebugCheckbox");

        // Populate dropdown from enum
        _schoolPicker.Clear();
        foreach (CardSchool school in System.Enum.GetValues(typeof(CardSchool)))
            _schoolPicker.AddItem(school.ToString(), (int)school);

        // Default to Arcanist
        _schoolPicker.Selected = (int)CardSchool.Arcanist;
        UpdateDescription();

        _schoolPicker.ItemSelected += (_) => UpdateDescription();
        _confirmButton.Pressed += OnConfirm;

		_debugCheckbox.ButtonPressed = false;
    }

    private void UpdateDescription()
    {
        var school = (CardSchool)_schoolPicker.GetSelectedId();
        int count = 0;
        foreach (var bp in CardDatabase.Blueprints)
            if (bp.School == school) count++;

        PlayerSession.SelectedSchool = school;
        _descriptionLabel.Text = Descriptions.TryGetValue(school, out var desc)
            ? $"{desc}\n\n{count} cards available."
            : $"{count} cards available.";
    }

	private void OnConfirm()
	{
		PlayerSession.SelectedSchool = (CardSchool)_schoolPicker.GetSelectedId();
		PlayerSession.DebugMode = _debugCheckbox.ButtonPressed;
		GetTree().ChangeSceneToFile(BattlefieldScenePath);
	}
}