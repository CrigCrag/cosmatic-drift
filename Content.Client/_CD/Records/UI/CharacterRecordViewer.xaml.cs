using System.Linq;
using Content.Client.UserInterface.Controls;
using Content.Shared._CD.Records;
using Content.Shared.Administration;
using Content.Shared.Security;
using Content.Shared.StationRecords;
using Robust.Client.AutoGenerated;
using Robust.Client.UserInterface.XAML;

namespace Content.Client._CD.Records.UI;

[GenerateTypedNameReferences]
public sealed partial class CharacterRecordViewer : FancyWindow
{
    public event Action<uint?, uint?>? OnListingItemSelected;
    public event Action<StationRecordFilterType, string?>? OnFiltersChanged;

    private bool _isPopulating;
    private StationRecordFilterType _filterType;

    private RecordConsoleType? _type;

    private readonly RecordEntryViewPopup _entryView = new();
    private List<CharacterRecords.RecordEntry>? _entries;

    private DialogWindow? _wantedReasonDialog;
    public event Action<string?>? OnSetWantedStatus;
    public event Action<SecurityStatus>? OnSetSecurityStatus;

    public uint? SecurityWantedStatusMaxLength;

    public CharacterRecordViewer()
    {
        RobustXamlLoader.Load(this);

        // There is no reason why we can't just steal the StationRecordFilter class.
        // If wizden adds a new kind of filtering we want to replicate it here.
        foreach (var item in Enum.GetValues<StationRecordFilterType>())
        {
            RecordFilterType.AddItem(GetTypeFilterLocals(item), (int)item);
        }

        // Again, if wizden changes something about Criminal Records, we want to replicate the
        // functionality here.
        foreach (var status in Enum.GetValues<SecurityStatus>())
        {
            var name = Loc.GetString($"criminal-records-status-{status.ToString().ToLower()}");
            StatusOptionButton.AddItem(name, (int)status);
        }

        RecordListing.OnItemSelected += _ =>
        {
            if (!RecordListing.GetSelected().Any())
                return;
            var selected = RecordListing.GetSelected().First();
            var (index, listingKey) = ((uint, uint?))selected.Metadata!;
            OnListingItemSelected?.Invoke(index,  listingKey);
        };

        RecordListing.OnItemDeselected += _ =>
        {
            // When we populate the records, we clear the contents of the listing.
            // This could cause a deselection but we don't want to really deselect because it would
            // interrupt what the player is doing.
            if (!_isPopulating)
                OnListingItemSelected?.Invoke(null, null);
        };

        RecordFilters.OnPressed += _ =>
        {
            OnFiltersChanged?.Invoke(_filterType, RecordFiltersValue.Text);
        };

        RecordFiltersReset.OnPressed += _ =>
        {
            OnFiltersChanged?.Invoke(StationRecordFilterType.Name, null);
        };

        RecordFilterType.OnItemSelected += eventArgs =>
        {
            var type = (StationRecordFilterType)eventArgs.Id;
            _filterType = type;
            RecordFilterType.SelectId(eventArgs.Id);
        };

        RecordEntryViewButton.OnPressed += _ =>
        {
            if (_entries == null || !RecordEntryList.GetSelected().Any())
                return;
            int idx = RecordEntryList.IndexOf(RecordEntryList.GetSelected().First());
            _entryView.SetContents(_entries[idx]);
            _entryView.Open();
        };

        StatusOptionButton.OnItemSelected += args =>
        {
            var status = (SecurityStatus)args.Id;
            if (status == SecurityStatus.Wanted)
                SetWantedStatus();
            else
                OnSetSecurityStatus?.Invoke(status);
        };

        OnClose += () => _entryView.Close();

        // Admin console entry type selector
        RecordEntryViewType.AddItem(Loc.GetString("department-Security"));
        RecordEntryViewType.AddItem(Loc.GetString("department-Medical"));
        RecordEntryViewType.AddItem(Loc.GetString("humanoid-profile-editor-cd-records-employment"));
        RecordEntryViewType.OnItemSelected += args =>
        {
            RecordEntryViewType.SelectId(args.Id);
            // This is a hack to get the server to send us another packet with the new entries
            OnFiltersChanged?.Invoke(_filterType, RecordFiltersValue.Text);
        };
    }

    // If we are using wizden's class we might as well use their localization.
    private string GetTypeFilterLocals(StationRecordFilterType type)
    {
        return Loc.GetString($"general-station-record-{type.ToString().ToLower()}-filter");
    }

    public void UpdateState(CharacterRecordConsoleState state)
    {
        #region Visibility

        RecordEntryViewType.Visible = false;
        _type = state.ConsoleType;

        // Disable listing if we don't have one selected
        if (state.RecordListing == null)
        {
            RecordListingStatus.Visible = true;
            RecordListing.Visible = false;
            RecordListingStatus.Text = Loc.GetString("cd-record-viewer-empty-state");
            RecordContainer.Visible = false;
            RecordContainerStatus.Visible = false;
            return;
        }

        RecordListingStatus.Visible = false;
        RecordListing.Visible = true;

        // Enable extended filtering only for admin and security consoles
        switch (_type)
        {
            case RecordConsoleType.Employment:
                RecordFilterType.Visible = false;
                RecordFilterType.SelectId((int)StationRecordFilterType.Name);

                Title = Loc.GetString("cd-character-records-viewer-title-employ");
                break;
            case RecordConsoleType.Medical:
                RecordFilterType.Visible = false;
                RecordFilterType.SelectId((int)StationRecordFilterType.Name);

                Title = Loc.GetString("cd-character-records-viewer-title-med");
                break;
            case RecordConsoleType.Security:
                RecordFilterType.Visible = true;

                Title = Loc.GetString("cd-character-records-viewer-title-sec");
                break;
            case RecordConsoleType.Admin:
                RecordFilterType.Visible = true;
                Title = "Admin records console";
                RecordEntryViewType.Visible = true;

                break;
        }

        #endregion

        #region PopulateListing

        if (state.Filter != null)
        {
            RecordFiltersValue.SetText(state.Filter.Value);
            RecordFilterType.SelectId((int) state.Filter.Type);
        }

        // If the counts are the same it is probably not needed to refresh the entry list. This provides
        // a much better UI experience at the cost of the user possibly needing to re-open the UI under
        // very specific circumstances that are *very* unlikely to appear in real gameplay.
        if (RecordListing.Count != state.RecordListing.Count)
        {
            _isPopulating = true;

            RecordListing.Clear();
            foreach (var (key, (txt, stationRecordsKey)) in state.RecordListing)
            {
                RecordListing.AddItem(txt, metadata: (key, stationRecordsKey));
            }

            _isPopulating = false;
        }

        #endregion

        #region FillRecordContainer

        // Enable container if we have a record selected
        if (state.SelectedRecord == null)
        {
            RecordContainerStatus.Visible = true;
            RecordContainer.Visible = false;
            return;
        }

        RecordContainerStatus.Visible = false;
        RecordContainer.Visible = true;

        var record = state.SelectedRecord!;
        var cr = record.CharacterRecords;

        // Basic info
        RecordContainerName.Text = record.Name;
        RecordContainerAge.Text = record.Age.ToString();
        RecordContainerJob.Text = record.JobTitle; /* At some point in the future we might want to display the icon */
        RecordContainerGender.Text = record.Gender.ToString();
        RecordContainerSpecies.Text = record.Species;
        RecordContainerHeight.Text = cr.Height + " " + UnitConversion.GetImperialDisplayLength(cr.Height);
        RecordContainerWeight.Text = cr.Weight + " " + UnitConversion.GetImperialDisplayMass(cr.Weight);
        RecordContainerContactName.SetMessage(cr.EmergencyContactName);

        RecordContainerEmployment.Visible = false;
        RecordContainerMedical.Visible = false;
        RecordContainerSecurity.Visible = false;

        switch (_type)
        {
            case RecordConsoleType.Employment:
                SetEntries(cr.EmploymentEntries);
                UpdateRecordBoxEmployment(record);
                break;
            case RecordConsoleType.Medical:
                SetEntries(cr.MedicalEntries);
                UpdateRecordBoxMedical(record);
                break;
            case RecordConsoleType.Security:
                SetEntries(cr.SecurityEntries);
                UpdateRecordBoxSecurity(record, state.SelectedSecurityStatus);
                break;
            case RecordConsoleType.Admin:
                UpdateRecordBoxEmployment(record);
                UpdateRecordBoxMedical(record);
                UpdateRecordBoxSecurity(record, state.SelectedSecurityStatus);
                switch ((RecordConsoleType) RecordEntryViewType.SelectedId)
                {
                case RecordConsoleType.Employment:
                    SetEntries(cr.EmploymentEntries, true);
                    break;
                case RecordConsoleType.Medical:
                    SetEntries(cr.MedicalEntries, true);
                    break;
                case RecordConsoleType.Security:
                    SetEntries(cr.SecurityEntries, true);
                    break;
                }
                break;
        }

        #endregion

    }

    private void SetEntries(List<CharacterRecords.RecordEntry> entries, bool addIndex = false)
    {
        _entries = entries;
        RecordEntryList.Clear();
        var i = 0;
        foreach (var entry in entries)
        {
            RecordEntryList.AddItem(addIndex ? $"({i.ToString()}) " + entry.Title : entry.Title);
            ++i;
        }
    }

    private void UpdateRecordBoxEmployment(FullCharacterRecords record)
    {
        RecordContainerEmployment.Visible = true;
        RecordContainerWorkAuth.Text = record.CharacterRecords.HasWorkAuthorization ? "yes" : "no";
    }

    private void UpdateRecordBoxMedical(FullCharacterRecords record)
    {
        RecordContainerMedical.Visible = true;
        var cr = record.CharacterRecords;
        RecordContainerMedical.Visible = true;
        RecordContainerAllergies.SetMessage(cr.Allergies, defaultColor: Color.White);
        RecordContainerDrugAllergies.SetMessage(cr.DrugAllergies, defaultColor: Color.White);
        RecordContainerPostmortem.SetMessage(cr.PostmortemInstructions, defaultColor: Color.White);
        RecordContainerSex.Text = record.Sex.ToString();
    }

    private void UpdateRecordBoxSecurity(FullCharacterRecords record, (SecurityStatus, string?)? criminal)
    {
        RecordContainerSecurity.Visible = true;
        RecordContainerIdentFeatures.SetMessage(record.CharacterRecords.IdentifyingFeatures, defaultColor: Color.White);
        RecordContainerFingerprint.Text = record.Fingerprint ?? Loc.GetString("cd-character-records-viewer-unknown");
        RecordContainerDNA.Text = record.DNA ?? Loc.GetString("cd-character-records-viewer-unknown");

        RecordContainerWantedReason.Visible = false;
        if (criminal != null)
        {
            var (stat, reason) = criminal.Value;
            StatusOptionButton.Select((int)stat);
            RecordContainerWantedReason.Text = reason;
            RecordContainerWantedReason.Visible = reason != null;
        }
    }

    // This is copied almost verbatim from CriminalRecordsConsoleWindow.xaml.cs
    private void SetWantedStatus()
    {
        if (_wantedReasonDialog != null)
        {
            _wantedReasonDialog.MoveToFront();
            return;
        }

        const string field = "reason";
        var title = Loc.GetString("criminal-records-status-wanted");
        var placeholder = Loc.GetString("cd-character-records-viewer-setwanted-placeholder");
        var prompt = Loc.GetString("criminal-records-console-reason");
        var entry = new QuickDialogEntry(field, QuickDialogEntryType.LongText, prompt, placeholder);
        var entries = new List<QuickDialogEntry>() { entry };
        _wantedReasonDialog = new DialogWindow(title, entries);

        _wantedReasonDialog.OnConfirmed += responses =>
        {
            var reason = responses[field];
            if (reason.Length < 1 || reason.Length > SecurityWantedStatusMaxLength)
                return;

            OnSetWantedStatus?.Invoke(reason);
        };

        _wantedReasonDialog.OnClose += () => { _wantedReasonDialog = null; };
    }
    public bool IsSecurity()
    {
        return _type == RecordConsoleType.Security || _type == RecordConsoleType.Admin;
    }

    public void SetSecurityStatusEnabled(bool setting)
    {
        for (var i = 0; i < StatusOptionButton.ItemCount; ++i)
        {
            StatusOptionButton.SetItemDisabled(i, !setting);
        }
    }
}

