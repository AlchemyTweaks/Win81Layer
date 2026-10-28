using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

#nullable enable annotations

namespace Win81Layer;

// One search over the hub's own rows and pages (HubCatalog.SearchIndex) and the Windows entries of the shared system
// index (SystemSearchIndex.Current: Settings pages and settings, Control Panel items and tasks, tools), ranked by the
// same ranker as the Windows 7 Start menu (stems, Greek folding and accents, typos, synonyms), plus the Greek word
// typed with the English layout. Results replace the content pane; sidebar entries without a match dim.
public sealed partial class PcSettingsWindow
{
	internal enum HubResultGroup
	{
		Hub,
		// a Fix a problem page (Home's drill-ins): listed in a group of its own, never ahead of a setting
		Fix,
		Settings,
		ControlPanel,
		Tools
	}

	internal sealed record HubResult(SystemHit Hit, HubResultGroup Group, int Score, string? RowId, string? PageId);

	private bool _searchActive;

	private string _beforeSearch = "home";

	private DispatcherTimer? _searchTimer;

	private DispatcherTimer? _indexPoll;

	private readonly HashSet<HubResultGroup> _showAll = new HashSet<HubResultGroup>();

	private List<HubResult> _results = new List<HubResult>();

	internal IReadOnlyList<HubResult> QaResults => _results;

	internal double QaLastRankMs { get; private set; }

	internal int QaSearchRuns { get; private set; }

	internal bool QaSearchActive => _searchActive;

	private void OnSearchTextChanged()
	{
		ApplySearchLook();
		if (_search.Text.Trim().Length == 0)
		{
			if (_searchActive)
			{
				ClearSearch(keepPage: true);
			}
			return;
		}
		_searchTimer ??= NewSearchTimer();
		_searchTimer.Stop();
		_searchTimer.Start();
	}

	private DispatcherTimer NewSearchTimer()
	{
		DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Input, base.Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(60.0)
		};
		t.Tick += delegate
		{
			t.Stop();
			RunSearch(_search.Text);
		};
		return t;
	}

	private void StopSearchTimers()
	{
		_searchTimer?.Stop();
		_searchTimer = null;
		_indexPoll?.Stop();
		_indexPoll = null;
	}

	private void OnSearchKey(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Down && _searchActive)
		{
			FocusContent();
			e.Handled = true;
		}
		else if (e.Key == Key.Enter)
		{
			_searchTimer?.Stop();
			if (_search.Text.Trim().Length > 0)
			{
				RunSearch(_search.Text);
				if (_results.Count > 0)
				{
					OpenResult(_results[0]);
				}
			}
			e.Handled = true;
		}
	}

	// Leaves the search: the text is cleared and the page shown before it comes back.
	private void ClearSearch(bool keepPage)
	{
		_searchTimer?.Stop();
		_indexPoll?.Stop();
		bool wasActive = _searchActive;
		_searchActive = false;
		if (_search != null && _search.Text.Length > 0)
		{
			_search.Text = string.Empty;
		}
		_showAll.Clear();
		DimSidebar(null);
		if (wasActive && keepPage)
		{
			_content.Animate = false;
			try
			{
				Navigate(_beforeSearch, null, push: false);
			}
			finally
			{
				_content.Animate = true;
			}
		}
	}

	private void OnIndexReady()
	{
		if (_closed)
		{
			return;
		}
		RefreshSidebarVisibility();
		if (_searchActive)
		{
			RunSearch(_search.Text);
		}
		else if (_current == "tools" || _current == "privacy")
		{
			RebuildCurrentPage();
		}
	}

	// Ranks the query and shows the results page.
	internal void RunSearch(string raw)
	{
		string q = (raw ?? string.Empty).Trim();
		if (q.Length == 0)
		{
			ClearSearch(keepPage: true);
			return;
		}
		Stopwatch sw = Stopwatch.StartNew();
		_results = Rank(q);
		sw.Stop();
		QaLastRankMs = sw.Elapsed.TotalMilliseconds;
		QaSearchRuns++;
		if (!_searchActive)
		{
			_beforeSearch = _current.Length == 0 || _current == "search" ? "home" : _current;
			_searchActive = true;
		}
		_current = "search";
		_pageTitle.Text = "Search";
		SelectNav("");
		UpdateChangedToggle();
		DimSidebar(_results);
		_content.Animate = false;
		try
		{
			ShowPage(() => SearchPage(q));
		}
		finally
		{
			_content.Animate = true;
		}
		// The Windows entries arrive later on a cold start: search again once they are there.
		if (SystemSearchIndex.Current == null && SettingsWorld.IsReal)
		{
			_indexPoll ??= NewIndexPoll();
			_indexPoll.Start();
		}
	}

	private DispatcherTimer NewIndexPoll()
	{
		DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Background, base.Dispatcher)
		{
			Interval = TimeSpan.FromMilliseconds(400.0)
		};
		t.Tick += delegate
		{
			if (!_searchActive || _closed)
			{
				t.Stop();
				return;
			}
			if (SystemSearchIndex.Current != null)
			{
				t.Stop();
				RunSearch(_search.Text);
			}
		};
		return t;
	}

	// The ranking (UI thread, a few milliseconds): the query, the Greek word typed with the English layout (-1), each
	// hub hit +20 so the hub wins ties; Windows entries whose target is a hub row's link fold into that row.
	internal List<HubResult> Rank(string raw)
	{
		List<HubResult> list = new List<HubResult>();
		SearchQuery query = SearchQuery.Build(raw, uiGreek: false);
		string greekKeys = GreekLayout.ToGreekKeys(raw);
		SearchQuery? alt = greekKeys.Length > 0 && !string.Equals(greekKeys, raw, StringComparison.Ordinal) ? SearchQuery.Build(greekKeys, uiGreek: true) : null;
		Dictionary<string, int> hub = Best(HubCatalog.SearchIndex, query, alt);
		// Links of the hub rows (to fold the Windows entries that open the same page).
		Dictionary<string, string> linkToRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (HubRow r in HubCatalog.Rows)
		{
			if (r.Link?.Uri != null && !linkToRow.ContainsKey(r.Link.Uri))
			{
				linkToRow[r.Link.Uri] = r.Id;
			}
		}
		Dictionary<string, SystemHit> hubHits = HubCatalog.SearchIndex.Hits.ToDictionary((SystemHit h) => h.Id, StringComparer.Ordinal);
		foreach ((string id, int score) in hub)
		{
			SystemHit h = hubHits[id];
			string key = id.Substring(4);
			HubRow? row = HubCatalog.Row(key);
			HubPage? page = row == null ? HubCatalog.Page(key) : null;
			string pageId = row?.Page ?? page?.Id ?? "home";
			if (!PageShown(pageId) || (row != null && !RowShown(row)))
			{
				continue;
			}
			HubResultGroup group = row == null && pageId.StartsWith("fix/", StringComparison.Ordinal) ? HubResultGroup.Fix : HubResultGroup.Hub;
			list.Add(new HubResult(h, group, score + 20, row?.Id, pageId));
		}
		SystemSearchIndex? index = SystemSearchIndex.Current;
		if (index != null)
		{
			SearchQuery wq = SearchQuery.Build(raw, index.UiGreek);
			SearchQuery? walt = alt == null ? null : SearchQuery.Build(greekKeys, uiGreek: true);
			Dictionary<string, SystemHit> byId = new Dictionary<string, SystemHit>(StringComparer.Ordinal);
			foreach ((string id, int score) in Best(index, wq, walt, byId))
			{
				SystemHit h = byId[id];
				if (h.Launch == SystemLaunchKind.Uri && linkToRow.TryGetValue(h.Target, out string? rowId))
				{
					// the Windows page is the link of a hub row: the hub row stands for it (with the better score)
					int i = list.FindIndex((HubResult r) => r.RowId == rowId);
					if (i >= 0)
					{
						if (score + 20 > list[i].Score)
						{
							list[i] = list[i] with { Score = score + 20 };
						}
						continue;
					}
				}
				HubResultGroup g = h.Kind switch
				{
					SystemHitKind.Tool => HubResultGroup.Tools,
					SystemHitKind.ControlPanelItem or SystemHitKind.ControlPanelTask => HubResultGroup.ControlPanel,
					_ => HubResultGroup.Settings
				};
				if (g == HubResultGroup.ControlPanel && _caps != null && _caps.NoControlPanel)
				{
					continue;
				}
				list.Add(new HubResult(h, g, score + h.Prior, null, null));
			}
		}
		// Home's Quick card copies give way to the row they copy ("Brightness" once, on Displays).
		HashSet<string> rowIds = new HashSet<string>(list.Where((HubResult r) => r.RowId != null).Select((HubResult r) => r.RowId!), StringComparer.OrdinalIgnoreCase);
		list.RemoveAll((HubResult r) => r.RowId != null && HubCatalog.QuickCopies.TryGetValue(r.RowId, out string? canonical) && rowIds.Contains(canonical));
		// The problems people describe come after every setting and page (their own group on the results page), unless the
		// query names the problem itself: a full match tier (30) above the best setting ("no sound").
		int bestOther = list.Where((HubResult r) => r.Group != HubResultGroup.Fix).Select((HubResult r) => r.Score).DefaultIfEmpty(int.MinValue / 2).Max();
		bool Demoted(HubResult r) => r.Group == HubResultGroup.Fix && r.Score < bestOther + 30;
		list.Sort(delegate(HubResult a, HubResult b)
		{
			int c = (Demoted(a) ? 1 : 0).CompareTo(Demoted(b) ? 1 : 0);
			if (c != 0)
			{
				return c;
			}
			// the match tier first; within it, a hub page the query names ("power", "οθόνη") before its settings
			c = MatchTier(b.Score).CompareTo(MatchTier(a.Score));
			if (c != 0)
			{
				return c;
			}
			c = (NamedPage(a) ? 0 : 1).CompareTo(NamedPage(b) ? 0 : 1);
			if (c != 0)
			{
				return c;
			}
			c = b.Score.CompareTo(a.Score);
			if (c != 0)
			{
				return c;
			}
			c = (a.Group == HubResultGroup.Hub ? 0 : 1).CompareTo(b.Group == HubResultGroup.Hub ? 0 : 1);
			if (c != 0)
			{
				return c;
			}
			// a setting before its page at the same score (the query named the setting)
			c = (a.RowId != null ? 0 : 1).CompareTo(b.RowId != null ? 0 : 1);
			if (c != 0)
			{
				return c;
			}
			c = a.Hit.KindOrder.CompareTo(b.Hit.KindOrder);
			return c != 0 ? c : string.Compare(a.Hit.Name, b.Hit.Name, StringComparison.CurrentCultureIgnoreCase);
		});
		return list;
	}

	// The ranker's match tiers (exact name, prefix, word sequence, acronym, word start, keywords), at least 30 apart; the
	// hub's +20 and the small penalties stay inside a tier.
	private static int MatchTier(int score)
	{
		if (score >= SearchScore.Exact - 10)
		{
			return 6;
		}
		if (score >= SearchScore.Prefix - 10)
		{
			return 5;
		}
		if (score >= SearchScore.WordSeq - 10)
		{
			return 4;
		}
		if (score >= SearchScore.AcronymPrefix - 10)
		{
			return 3;
		}
		if (score >= SearchScore.WordStart - 10)
		{
			return 2;
		}
		return score >= SearchScore.Substring - 10 ? 1 : 0;
	}

	// A hub page matched by one of its names (not only by a keyword).
	private static bool NamedPage(HubResult r) => r.Group == HubResultGroup.Hub && r.RowId == null && r.Score - 20 >= SearchScore.WordStart - 10;

	private static Dictionary<string, int> Best(SystemSearchIndex index, SearchQuery q, SearchQuery? alt, Dictionary<string, SystemHit>? hits = null)
	{
		Dictionary<string, int> best = new Dictionary<string, int>(StringComparer.Ordinal);
		void Take(SearchQuery query, int penalty)
		{
			foreach ((SystemHit hit, int match) in index.Rank(query))
			{
				int s = match + penalty;
				if (!best.TryGetValue(hit.Id, out int cur) || s > cur)
				{
					best[hit.Id] = s;
					if (hits != null)
					{
						hits[hit.Id] = hit;
					}
				}
			}
		}
		Take(q, 0);
		if (alt != null)
		{
			Take(alt, -1);
		}
		return best;
	}

	// A page the sidebar shows (sub-pages follow their parent).
	private bool PageShown(string pageId)
	{
		string top = TopLevel(pageId);
		if (top == "account")
		{
			return true;
		}
		return EntryVisible(top);
	}

	// A row that exists on this PC: the page builders' own gating (HubCatalog.RowAvailable), a link row needs a target
	// (and is not one that the page folds into the row that opens the same Windows page), a touchpad row a touchpad page.
	internal bool RowShown(HubRow r)
	{
		if (r.Kind == HubRowKind.Link && r.Link != null && _caps != null && !SettingsLink.Available(r.Link, _caps))
		{
			return false;
		}
		if (r.Kind == HubRowKind.Link && r.Link != null && LinkFolded(r.Id, r.Link))
		{
			return false;
		}
		if (!HubCatalog.RowAvailable(r, _caps))
		{
			return false;
		}
		if (_start == null && r.Card == "Background" && r.Page == "start")
		{
			// the Start background rows need the Start screen (a window opened without one has none)
			return false;
		}
		if (r.Id == "home.favorites")
		{
			// Home has the card only while something is starred
			return FavoriteList().Count > 0;
		}
		return !r.Id.StartsWith("recent.", StringComparison.Ordinal);
	}

	private void DimSidebar(List<HubResult>? results)
	{
		HashSet<string>? hit = results == null ? null : new HashSet<string>(results.Where((HubResult r) => r.PageId != null).Select((HubResult r) => TopLevel(r.PageId!)), StringComparer.Ordinal);
		foreach (NavEntry e in _entries)
		{
			e.Item.Opacity = hit != null && !hit.Contains(e.Id) ? 0.4 : 1.0;
		}
	}

	private static readonly (HubResultGroup Group, string Title, int Count)[] GroupOrder = new (HubResultGroup, string, int)[5]
	{
		(HubResultGroup.Fix, "Fix a problem", 3),
		(HubResultGroup.Hub, "In PC settings", 8),
		(HubResultGroup.Settings, "Windows Settings", 6),
		(HubResultGroup.ControlPanel, "Control Panel", 6),
		(HubResultGroup.Tools, "Tools", 4)
	};

	private ScrollViewer SearchPage(string q)
	{
		Pg pg = new Pg(this, "search");
		if (_results.Count == 0)
		{
			pg.Block(new TextBlock
			{
				Text = SystemSearchIndex.Current == null && SettingsWorld.IsReal ? "Searching…" : "No settings match “" + q + "”.",
				FontFamily = SegoeUi,
				FontSize = 13.5,
				Foreground = _pal.Ink2,
				Margin = new Thickness(12.0, 12.0, 12.0, 0.0),
				TextWrapping = TextWrapping.Wrap
			});
			pg.Card();
			pg.Add(LinkRow("home.windows", "Search in Windows Settings"));
			return pg.Done();
		}
		HubResult top = _results[0];
		pg.Header("Top hit");
		pg.Add(TopHitRow(top));
		foreach ((HubResultGroup group, string title, int count) in GroupOrder)
		{
			List<HubResult> items = _results.Where((HubResult r) => r.Group == group && !ReferenceEquals(r, top)).ToList();
			if (items.Count == 0)
			{
				continue;
			}
			int show = _showAll.Contains(group) ? items.Count : Math.Min(count, items.Count);
			pg.Header(title);
			for (int i = 0; i < show; i++)
			{
				pg.Add(ResultRow(items[i]));
			}
			if (show < items.Count)
			{
				HubResultGroup g = group;
				pg.Add(ClickRow("Show all " + items.Count, null, MenuGlyphs.ChevronPath(_pal.Ink2), delegate
				{
					_showAll.Add(g);
					RunSearch(_search.Text);
				}));
			}
		}
		return pg.Done();
	}

	// The top hit: a hub row with its own live control (taken from a fresh build of its page), a hub page, or the
	// Windows entry. The launcher's startup, shell, rendering and recovery rows open their page instead.
	private FrameworkElement TopHitRow(HubResult r)
	{
		if (r.RowId != null && r.PageId != null && !HubCatalog.NoLiveTopHit.Contains(r.RowId))
		{
			try
			{
				BuildPageCore(r.PageId);
				if (_rowElements.TryGetValue(r.RowId, out FrameworkElement? live) && live.Parent is Panel parent)
				{
					parent.Children.Remove(live);
					live.Margin = new Thickness(0.0);
					return live;
				}
			}
			catch (Exception ex)
			{
				Logger.Log("PC settings search: live row " + r.RowId + " failed: " + ex.Message);
			}
		}
		return ResultRow(r);
	}

	private FrameworkElement ResultRow(HubResult r)
	{
		if (r.Group == HubResultGroup.Fix)
		{
			return ClickRow(r.Hit.Name, "Fix a problem", MenuGlyphs.ChevronPath(_pal.Ink2), delegate { OpenResult(r); });
		}
		if (r.Group == HubResultGroup.Hub)
		{
			// a page is named as the sidebar names it ("Power" on a PC without battery and sleep)
			string name = r.RowId == null && r.PageId != null ? PageTitle(r.PageId) : r.Hit.Name;
			// a drill-in page says where it is ("Power"), a top-level page that it is a page
			string path = r.RowId != null ? PathOf(r.RowId) : (HubCatalog.Page(r.PageId ?? "")?.Parent is string parent ? PageTitle(parent) : "Page");
			return ClickRow(name, path, MenuGlyphs.ChevronPath(_pal.Ink2), delegate { OpenResult(r); });
		}
		string detail = r.Group switch
		{
			HubResultGroup.ControlPanel => "Control Panel",
			HubResultGroup.Tools => "Tool",
			_ => r.Hit.Kind == SystemHitKind.Security ? "Windows Security" : "Windows Settings"
		};
		if (!string.IsNullOrEmpty(r.Hit.Detail))
		{
			detail = detail + " › " + r.Hit.Detail;
		}
		return ClickRow(r.Hit.Name, detail, TrailingGlyph(""), delegate { OpenResult(r); });
	}

	// "Mouse › Pointer speed"
	private string PathOf(string rowId)
	{
		HubRow? row = HubCatalog.Row(rowId);
		if (row == null)
		{
			return string.Empty;
		}
		string page = PageTitle(row.Page);
		HubPage? p = HubCatalog.Page(row.Page);
		if (p?.Parent != null)
		{
			page = PageTitle(p.Parent) + " › " + page;
		}
		return string.Equals(row.Card, PageTitle(row.Page), StringComparison.Ordinal) ? page : page + " › " + row.Card;
	}

	internal void OpenResult(HubResult r)
	{
		if ((r.Group == HubResultGroup.Hub || r.Group == HubResultGroup.Fix) && r.PageId != null)
		{
			string page = r.PageId;
			string? row = r.RowId;
			_searchActive = false;
			_searchTimer?.Stop();
			_search.Text = string.Empty;
			DimSidebar(null);
			Navigate(page, row, push: true);
			return;
		}
		bool elevate = r.Hit.CanElevate && r.Hit.Elevate;
		SettingsLink.Run(r.Hit, elevate);
	}
}
