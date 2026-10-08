using System.Collections.Generic;
using Logzilla.App.Infrastructure;
using Logzilla.Core.Logs;
using Logzilla.Core.Query;

namespace Logzilla.App.Views;

// Each open tab keeps its own search: the query, the severity chips, the sources shown,
// the time range and the field filters. They used to belong to the window, so a search
// typed on one log followed you to every other tab and filtered it too.
//
// The search options — regular expression, match case, highlight or filter, context
// lines — stay with the window: they are how you like to search, not what you are
// looking for in one particular file.

internal sealed partial class MainForm
{
    private sealed class TabSearch
    {
        public string Query = "";
        public int LevelMask = 0x7F;
        public long SrcMask = -1;
        public string TimeText = "";
        public FieldFilter[] Fields = [];
    }

    private readonly Dictionary<LogDocument, TabSearch> _tabSearch = new();

    /// <summary>The tab whose search the controls are showing.</summary>
    private LogDocument _searchOwner;

    /// <summary>
    /// Makes tab <paramref name="index"/> the active one: puts the current tab's search
    /// away and brings back the one the new tab had — empty for a tab never seen. Does not
    /// start a filter; callers do that once they are ready.
    /// </summary>
    private void SwitchTab(int index)
    {
        if (_searchOwner != null && _docs.Contains(_searchOwner))
        {
            _tabSearch[_searchOwner] = new TabSearch
            {
                Query = _q.Text,
                LevelMask = _levelMask,
                SrcMask = _srcMask,
                TimeText = _tRange.Text,
                Fields = _fieldFilters.ToArray(),
            };
        }
        _active = index;
        var doc = F;
        _searchOwner = doc;
        var s = doc != null && _tabSearch.TryGetValue(doc, out var saved) ? saved : new TabSearch();

        _view.ClearSelection();
        _debounce.Stop();
        _q.Text = s.Query;
        _debounce.Stop();          // the box's TextChanged starts it; the caller filters
        _levelMask = s.LevelMask;
        _srcMask = s.SrcMask;
        _fieldFilters.Clear();
        _fieldFilters.AddRange(s.Fields);
        _updatingTimeRange = true;
        _tRange.Text = s.TimeText;
        _updatingTimeRange = false;
        _debounce.Stop();
        ApplyTimeRangeText();
        _bTRangeClear.ForeColor = _tRange.Text.Length > 0 ? Theme.SevColor[4] : Theme.TextDim;
        UpdateTimeTrigger();
        SyncFieldBar();
        SyncLevels();
        SetToggleStates();
    }

    /// <summary>A closed tab's search goes with it.</summary>
    private void ForgetTabSearch(LogDocument doc)
    {
        _tabSearch.Remove(doc);
        if (ReferenceEquals(_searchOwner, doc))
        {
            _searchOwner = null;
        }
    }
}
