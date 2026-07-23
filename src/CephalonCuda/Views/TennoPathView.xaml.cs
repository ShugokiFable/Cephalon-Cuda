using System.Windows;
using System.Windows.Controls;
using CephalonCuda.Models;
using Microsoft.Win32;

namespace CephalonCuda.Views;

public partial class TennoPathView : UserControl
{
    private string _lastPurchase = "";
    public TennoPathView()
    {
        InitializeComponent();
        PhaseFilter.ItemsSource = new[] { "All phases", "1 · FOUNDATION", "2 · STAR CHART", "3 · POWER LAYER", "4 · LATE GAME", "5 · ENDGAME" };
        PhaseFilter.SelectedIndex = 0;
        GoalCategoryBox.ItemsSource = new[] { "Progression", "Build", "Farm", "Quest", "Mastery", "Economy", "Reputation", "Cosmetic", "General" };
        GoalCategoryBox.SelectedItem = "General";
        GoalPriorityBox.ItemsSource = new[] { "High", "Normal", "Low" };
        GoalPriorityBox.SelectedIndex = 1;
        Loaded += (_, _) =>
        {
            RefreshAll();
            if (App.Services.Returns.ShouldAutoBrief)
                ReturnTab.IsSelected = true;
            else if (!App.Services.Settings.FirstRunCompleted)
                RoadmapTab.IsSelected = true;
            App.Services.Settings.FirstRunCompleted = true;
        };
    }

    private void RefreshAll()
    {
        var s = App.Services.Onboarding;
        var p = s.Progress();
        ProgressLabel.Text = $"{p.Done}/{p.Total} core objectives · {p.Percent}%";
        RoadmapProgress.Value = p.Percent;
        RoutineLabel.Text = App.Services.Settings.RoutineBudget;
        RefreshRoadmap();
        RefreshGoals();
        CodesGrid.ItemsSource = s.GetCodes();
        RefreshReturnBriefing();
    }

    private void RefreshReturnBriefing()
    {
        var b = App.Services.Returns.GetBriefing();
        ReturnHeadline.Text = b.Headline;
        ReturnSummary.Text = b.Summary;
        ReturnLastPlayed.Text = $"Last activity: {b.LastPlayedDisplay}" + (b.LastPlayedAt is null ? "" : $" · {b.DaysAway} day(s) ago");
        ReturnNowList.ItemsSource = b.DoNow;
        ReturnCheckList.ItemsSource = b.CheckBeforeInvesting;
        ReturnDataList.ItemsSource = b.FreshData;
        AutoReturnBox.IsChecked = App.Services.Settings.AutoReturnBriefing;
        AutoDataBox.IsChecked = App.Services.Settings.AutoRefreshData;
    }

    private void RefreshRoadmap()
    {
        var tasks = App.Services.Onboarding.GetRoadmap(ShowFinished.IsChecked == true);
        if (PhaseFilter.SelectedItem is string phase && phase != "All phases") tasks = tasks.Where(t => t.Phase == phase).ToList();
        RoadmapList.ItemsSource = tasks;
    }

    private void RefreshGoals()
    {
        var goals = App.Services.Onboarding.GetGoals();
        GoalsList.ItemsSource = goals;
        GoalSummaryList.ItemsSource = goals.Take(6).ToList();
    }

    private void OnRoadmapSelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshRoadmap(); }
    private void OnRoadmapFilterChanged(object sender, RoutedEventArgs e) { if (IsLoaded) RefreshRoadmap(); }

    private void OnTaskDone(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RoadmapTask t) return;
        App.Services.Onboarding.SetTaskState(t.Id, completed: !t.Completed, skipped: false);
        RefreshAll();
    }
    private void OnTaskSkip(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RoadmapTask t) return;
        App.Services.Onboarding.SetTaskState(t.Id, completed: false, skipped: !t.Skipped);
        RefreshAll();
    }
    private void OnTaskPin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RoadmapTask t) return;
        App.Services.Onboarding.SetTaskState(t.Id, pinned: !t.Pinned);
        RefreshAll();
    }
    private void OnTaskAsk(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RoadmapTask t) return;
        Ask($"Help me complete this Warframe objective efficiently: {t.Title}. Context: {t.Description}. Why it matters: {t.WhyItMatters}. Give me exact prerequisites, where to go, what to build, what not to waste, and a budget path for my current account.");
    }
    private void OnAskNext(object sender, RoutedEventArgs e) => Ask("Based on my tracked roadmap, goals, inventory, MR, live world state and market data, what should I do next in Warframe? Give me a prioritized plan that fits my configured routine budget and warn me about traps or prerequisites.");

    private void OnAskReturnPlan(object sender, RoutedEventArgs e)
    {
        App.Services.Returns.MarkBriefed();
        Ask("I just returned to Warframe. Build a precise re-entry plan from my last-played gap, tracked account state, goals, Foundry, official drop database, live world state, current item data, market data and any imported build evidence. Separate: claim/check now, one dependable loadout to repair, progression blockers, time-limited opportunities, what changed that actually affects me, bad purchases to avoid, and a session plan for my configured time budget. Do not overwhelm me with unrelated systems.");
    }

    private void OnMarkPlayedToday(object sender, RoutedEventArgs e)
    {
        App.Services.Returns.MarkPlayedNow();
        RefreshReturnBriefing();
    }

    private async void OnRefreshReturnData(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        ReturnHeadline.Text = "REFRESHING LIVE INTELLIGENCE…";
        var failures = new List<string>();
        try
        {
            try { await App.Services.Intelligence.RefreshAllAsync(force: true); }
            catch (Exception ex) { failures.Add("items/drops/market: " + ex.Message); }

            try { await App.Services.RelicData.GetRelicsAsync(); }
            catch (Exception ex) { failures.Add("relics: " + ex.Message); }

            try { await App.Services.Mastery.SyncCatalogAsync(); }
            catch (Exception ex) { failures.Add("Mastery: " + ex.Message); }

            await App.Services.WorldState.RefreshAsync();
            if (!string.IsNullOrWhiteSpace(App.Services.WorldState.LastError))
                failures.Add("world state: " + App.Services.WorldState.LastError);

            if (App.Services.Settings.MarketUsername is { Length: > 0 } slug)
            {
                try { await App.Services.Market.GetUserOrdersAsync(slug); }
                catch (Exception ex) { failures.Add("personal listings: " + ex.Message); }
            }

        }
        finally
        {
            if (sender is Button b) b.IsEnabled = true;
            RefreshReturnBriefing();
            if (failures.Count == 0)
                ReturnHeadline.Text = "LIVE INTELLIGENCE READY";
            else
            {
                ReturnHeadline.Text = $"REFRESHED WITH {failures.Count} SOURCE WARNING{(failures.Count == 1 ? "" : "S")}";
                ReturnSummary.Text = string.Join(" · ", failures.Take(3));
            }
        }
    }

    private void OnReturnAutomationChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        App.Services.Settings.AutoReturnBriefing = AutoReturnBox.IsChecked == true;
        App.Services.Settings.AutoRefreshData = AutoDataBox.IsChecked == true;
    }

    private void OnAskReturnBuild(object sender, RoutedEventArgs e) => Ask("Audit the loadout I should restore first after returning. Use my tracked equipment and goals when available. Give a zero/low-Forma repair build, an upgrade ladder, a final target build, exact missing-mod acquisition routes, patch-age warnings, mission variants, and explicit stop points before expensive investment.");
    private void OnAskReturnChanges(object sender, RoutedEventArgs e) => Ask("I just returned to Warframe. Tell me only the changes, systems, rewards or build assumptions that materially affect my tracked progression and goals. Separate urgent, useful later, and safe to ignore. Use current source-stamped data and say when no verified change record is available.");
    private void OnAskReturnSession(object sender, RoutedEventArgs e) => Ask("Create an efficient 60-minute Warframe return session from my current goals, next roadmap task, live activities, ready Foundry items, standing/time gates and acquisition needs. Stack objectives where possible and include exact menu or mission navigation.");

    private void OnAskAcquire(object sender, RoutedEventArgs e)
    {
        var item = AcquireBox.Text.Trim(); if (item.Length == 0) return;
        Ask($"Give me the complete current acquisition path for '{item}' in Warframe. Resolve the exact variant; list every prerequisite, quest, planet, node/activity, faction rank, blueprint and component source, required resources, craft times, tradability, slot need, market alternative, and common mistakes. Use live/official data and say when a fact is unavailable.");
    }
    private void OnAskDisposition(object sender, RoutedEventArgs e)
    {
        var item = DispositionBox.Text.Trim(); if (item.Length == 0) return;
        Ask($"Before I destroy or consume '{item}', tell me whether to keep, sell for Credits, trade for Platinum, convert to Ducats, use as a crafting ingredient, or subsume it. Check Mastery, reacquisition difficulty, quest/event status, investment, set completion, current market and Ducat value, and safer alternatives.");
    }
    private void OnAskBuild(object sender, RoutedEventArgs e)
    {
        var item = BuildBox.Text.Trim(); if (item.Length == 0) return;
        Ask($"Build '{item}' for my actual Warframe account. Give me: zero/low-forma minimum viable build, upgrade ladder with replacement mods, final optimized build, forma order, required arcanes/companions/Helminth assumptions, mission and faction variants, acquisition sources for missing pieces, and what not to waste on. Compare community builds but do not copy them blindly.");
    }
    private void OnOpenQuestGuide(object sender, RoutedEventArgs e) => Services.OnboardingService.OpenUrl(Services.OnboardingService.OfficialQuestGuide);
    private void OnOpenDropTables(object sender, RoutedEventArgs e) => Services.OnboardingService.OpenUrl(Services.OnboardingService.OfficialDrops);

    private void OnOpenShopTab(object sender, RoutedEventArgs e) => ShopTab.IsSelected = true;
    private void OnEvaluatePurchase(object sender, RoutedEventArgs e)
    {
        _lastPurchase = PurchaseBox.Text.Trim();
        var v = App.Services.Onboarding.EvaluatePurchase(_lastPurchase);
        VerdictText.Text = v.VerdictDisplay;
        var verdictBrush = v.VerdictDisplay is "NEVER" or "AVOID" ? "Brush.Danger"
            : v.VerdictDisplay is "BUY" ? "Brush.Accent"
            : v.VerdictDisplay is "CONSIDER" or "COMPARE" or "PERSONAL" ? "Brush.Gold"
            : "Brush.Text";
        VerdictText.SetResourceReference(TextBlock.ForegroundProperty, verdictBrush);
        VerdictTitle.Text = v.Title;
        VerdictReason.Text = v.Reason;
        VerdictBetter.Text = v.BetterOption;
        VerdictIntel.Text = v.ItemIntel ?? "No exact local item match. The advisor can still compare the offer with its acquisition route and current market data.";
    }
    private void OnAskPurchase(object sender, RoutedEventArgs e)
    {
        var q = string.IsNullOrWhiteSpace(_lastPurchase) ? PurchaseBox.Text : _lastPurchase;
        Ask($"Should I buy this Warframe shop offer: '{q}'? Check the exact Platinum value, whether it is farmable, blueprint/drop route, included slot or Catalyst value, player-market alternatives, my progression stage, and what I should buy instead. Be blunt if it is bad value.");
    }

    private void OnOpenRedeem(object sender, RoutedEventArgs e) => Services.OnboardingService.OpenUrl(Services.OnboardingService.OfficialRedeem);
    private void OnAddCode(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Services.Onboarding.UpsertCode(CodeBox.Text, CodeTitleBox.Text, CodeRewardBox.Text, "unknown");
            CodeBox.Clear(); CodeTitleBox.Clear(); CodeRewardBox.Clear();
            CodesGrid.ItemsSource = App.Services.Onboarding.GetCodes();
            CodeStatus.Text = "Code saved. Redeem it on the official page, then mark it claimed.";
        }
        catch (Exception ex) { CodeStatus.Text = ex.Message; }
    }
    private void OnImportCodes(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON files|*.json|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try { CodeStatus.Text = $"Imported {App.Services.Onboarding.ImportCodes(dlg.FileName)} codes."; CodesGrid.ItemsSource = App.Services.Onboarding.GetCodes(); }
        catch (Exception ex) { CodeStatus.Text = "Import failed: " + ex.Message; }
    }
    private void OnRedeemCode(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RedeemCodeEntry c) return;
        Clipboard.SetText(c.Code);
        Services.OnboardingService.OpenUrl(Services.OnboardingService.OfficialRedeem);
        CodeStatus.Text = $"Copied {c.Code}. Paste it into the official redeem page.";
    }
    private void OnClaimCode(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RedeemCodeEntry c) return;
        App.Services.Onboarding.SetCodeClaimed(c.Code, !c.Claimed);
        CodesGrid.ItemsSource = App.Services.Onboarding.GetCodes();
    }

    private void OnAddGoal(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GoalTitleBox.Text)) return;
        var pri = GoalPriorityBox.SelectedIndex + 1;
        App.Services.Onboarding.AddGoal(GoalTitleBox.Text, GoalTargetBox.Text, GoalCategoryBox.SelectedItem?.ToString() ?? "General", pri, GoalNotesBox.Text);
        GoalTitleBox.Clear(); GoalTargetBox.Clear(); GoalNotesBox.Clear(); RefreshGoals();
    }
    private void OnCompleteGoal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PlayerGoal g) { App.Services.Onboarding.CompleteGoal(g.Id); RefreshGoals(); }
    }
    private void OnDeleteGoal(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is PlayerGoal g) { App.Services.Onboarding.DeleteGoal(g.Id); RefreshGoals(); }
    }
    private void OnAskGoalStack(object sender, RoutedEventArgs e) => Ask("Combine my active Warframe goals into the most efficient mission and crafting plan. Find activities that progress multiple goals at once, list prerequisites, and separate actions for today, this week, and later.");

    private static void Ask(string prompt)
    {
        if (Application.Current.MainWindow is MainWindow mw)
        {
            mw.NavigateTo("advisor");
            _ = mw.Dispatcher.BeginInvoke(() => App.Services.RequestAdvisorPrompt(prompt));
        }
        else App.Services.RequestAdvisorPrompt(prompt);
    }
}
