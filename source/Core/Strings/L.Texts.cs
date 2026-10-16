using System.Runtime.CompilerServices;

namespace San14Tenkazu;

// The texts of Strings/ko.txt by section and key: a property for a text, a method for a text with places, its
// parameters named as the places. A member reads the text of its own name (T), so the name is the key.
internal static partial class L
{
    internal static class Seasons
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Seasons)+"."+key);
        internal static string Label=>T();
        internal static string OnChoice=>T();
        internal static string OffChoice=>T();
        internal static string OffButton=>T();
        internal static string OnButton=>T();
        internal static string RecoverButton=>T();
        internal static string CancelButton=>T();
        internal static string Tip=>T();
        internal static string OnTip=>T();
        internal static string OffTip=>T();
        internal static string RecoverTip=>T();
        internal static string ConnectFirst=>T();
        internal static string VerifiedOnly=>T();
        internal static string OffStatus=>T();
        internal static string Connecting=>T();
        internal static string Restoring=>T();
        internal static string Reconnecting=>T();
        internal static string Restored=>T();
        internal static string Unrestored=>T();
        internal static string Spring=>T();
        internal static string Summer=>T();
        internal static string Autumn=>T();
        internal static string Winter=>T();
        internal static string RestartRequired=>T();
        internal static string CloseBlocked=>T();
        internal static string DisconnectBlocked=>T();
        internal static string Dated(object? season,object? year,object? month,object? day)=>Format(T(),(nameof(season),season),(nameof(year),year),(nameof(month),month),(nameof(day),day));
        internal static string Applying(object? dated)=>Format(T(),(nameof(dated),dated));
        internal static string TreeApplied(object? dated)=>Format(T(),(nameof(dated),dated));
        internal static string FullApplied(object? dated)=>Format(T(),(nameof(dated),dated));
        internal static string Failed(object? restored,object? error)=>Format(T(),(nameof(restored),restored),(nameof(error),error));
        internal static string Transition(object? current,object? next,object? percent)=>Format(T(),(nameof(current),current),(nameof(next),next),(nameof(percent),percent));
        internal static string LogFailed(object? error)=>Format(T(),(nameof(error),error));
    }
    // 창과 도구 막대
    internal static class Window
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Window) + "." + key);

        internal static string Cancel => T();
        internal static string ConnectForCards => T();
        internal static string TargetGone => T();
        internal static string NothingChanged => T();
        internal static string Title => T();
        internal static string Lists => T();
        internal static string ListsTip => T();
        internal static string Panel => T();
        internal static string PanelTip => T();
        internal static string PanelCloseTip => T();
        internal static string Log => T();
        internal static string Undo => T();
        internal static string Connect => T();
        internal static string ConnectTip => T();
        internal static string Reread => T();
        internal static string RereadTip => T();
        internal static string CancelSearch => T();
        internal static string CancelSearchTip => T();
        internal static string StartGame => T();
        internal static string DiscardAndClose => T();
        internal static string WelcomeCards => T();
        internal static string WelcomeWrites => T();
        internal static string WriteNow(object? what) => Format(T(), (nameof(what), what));
        internal static string AccessDenied => T();
        internal static string Close => T();
        internal static string Needs64Bit => T();
        internal static string Unhandled(object? type, object? message) => Format(T(), (nameof(type), type), (nameof(message), message));
        internal static string LeaveCard(object? count) => Format(T(), (nameof(count), count));
        internal static string Error(object? message) => Format(T(), (nameof(message), message));
    }

    // 게임 연결
    internal static class Connection
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Connection) + "." + key);

        internal static string SearchAfterQuick => T();
        internal static string SearchUnverified => T();
        internal static string Searching(object? done, object? total) => Format(T(), (nameof(done), done), (nameof(total), total));
        internal static string CheckingExe => T();
        internal static string FindingData => T();
        internal static string KnownPlaces => T();
        internal static string PlacesNotSaved => T();
        internal static string PlacesFound(object? count) => Format(T(), (nameof(count), count));
        internal static string Connected(object? pid, object? cityTable, object? counties, object? officers) => Format(T(), (nameof(pid), pid), (nameof(cityTable), cityTable), (nameof(counties), counties), (nameof(officers), officers));
        internal static string NoUnits => T();
        internal static string NoNameLists => T();
        internal static string Ready => T();
        internal static string ReadyReadOnly => T();
        internal static string NoGameState => T();
        internal static string SearchCancelled => T();
        internal static string Reread => T();
        internal static string ReadFailed => T();
        internal static string Disconnected(object? message) => Format(T(), (nameof(message), message));
        internal static string WorldReplaced => T();
    }

    // 검증하지 않은 게임 버전
    internal static class Builds
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Builds) + "." + key);

        internal static string ChecksPassed(object? count) => Format(T(), (nameof(count), count));
        internal static string ChecksFailed(object? count, object? failed) => Format(T(), (nameof(count), count), (nameof(failed), failed));
        internal static string CheckFailed(object? group, object? fact) => Format(T(), (nameof(group), group), (nameof(fact), fact));
        internal static string VerifiedStillWrites => T();
        internal static string Unverified(object? hash) => Format(T(), (nameof(hash), hash));
        internal static string AllowNotSaved => T();
        internal static string WritesAllowed => T();
        internal static string ReadOnly => T();
        internal static string TableOffsets(object? force, object? corps, object? map, object? county, object? building, object? officer, object? unit, object? state, object? country, object? item, object? personality, object? tactic, object? policy, object? ideology, object? office, object? rank) => Format(T(), (nameof(force), force), (nameof(corps), corps), (nameof(map), map), (nameof(county), county), (nameof(building), building), (nameof(officer), officer), (nameof(unit), unit), (nameof(state), state), (nameof(country), country), (nameof(item), item), (nameof(personality), personality), (nameof(tactic), tactic), (nameof(policy), policy), (nameof(ideology), ideology), (nameof(office), office), (nameof(rank), rank));
        internal static string AllowWrites(object? passed, object? blocked) => Format(T(), (nameof(passed), passed), (nameof(blocked), blocked));
    }

    // AUTO(데모) 플레이 중지
    internal static class Auto
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Auto) + "." + key);

        internal static string Detected => T();
        internal static string PauseFirst => T();
        internal static string Request(object? player, object? force) => Format(T(), (nameof(player), player), (nameof(force), force));
        internal static string Stopped(object? force) => Format(T(), (nameof(force), force));
        internal static string StoppedValuesOnly(object? force, object? why) => Format(T(), (nameof(force), force), (nameof(why), why));
        internal static string StoppedButChanged => T();
        internal static string DelegationNote => T();
        internal static string NoForces => T();
        internal static string Title => T();
        internal static string CurrentPlayer => T();
        internal static string LastPlayer => T();
        internal static string Choice(object? force, object? cities) => Format(T(), (nameof(force), force), (nameof(cities), cities));
        internal static string PlayThisForce => T();
        internal static string Stop => T();
        internal static string StopTip => T();
        internal static string SameForce(object? number, object? ruler, object? force) => Format(T(), (nameof(number), number), (nameof(ruler), ruler), (nameof(force), force));
        internal static string PickForce => T();
    }

    // 게임 함수 호출([AUTO 중지], [항복시키기])을 하지 못한 이유(진단)
    internal static class Calls
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Calls) + "." + key);

        internal static string WhyUnverified => T();
        internal static string WhyCode => T();
        internal static string WhyNoPage(object? code) => Format(T(), (nameof(code), code));
        internal static string WhyNoFrame(object? seconds) => Format(T(), (nameof(seconds), seconds));
        internal static string WhySkipped => T();
        internal static string WhyNoList => T();
        internal static string WhyListUnchanged(object? result) => Format(T(), (nameof(result), result));
        internal static string WhyNoCamera => T();
        internal static string WhyOtherCamera => T();
        internal static string WhyCameraHeld => T();
    }

    // 세력 편집 창 [항복시키기]
    internal static class Surrender
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Surrender) + "." + key);

        internal static string Button => T();
        internal static string ButtonTip => T();
        internal static string PickTarget(object? force) => Format(T(), (nameof(force), force));
        internal static string Confirm(object? from, object? to) => Format(T(), (nameof(from), from), (nameof(to), to));
        internal static string Request(object? from, object? to) => Format(T(), (nameof(from), from), (nameof(to), to));
        internal static string Done(object? from, object? to) => Format(T(), (nameof(from), from), (nameof(to), to));
        internal static string NoTarget => T();
        internal static string PlayerForce => T();
        internal static string PauseFirst => T();
        internal static string Refused => T();
        internal static string NotRun(object? why) => Format(T(), (nameof(why), why));
        internal static string Unconfirmed => T();
    }

    // 도시·관문 편집 창 [소속 주기]
    internal static class Give
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Give) + "." + key);

        internal static string Button => T();
        internal static string ButtonTip => T();
        internal static string PickTarget(object? place) => Format(T(), (nameof(place), place));
        internal static string Confirm(object? place, object? force) => Format(T(), (nameof(place), place), (nameof(force), force));
        internal static string Request(object? place, object? force) => Format(T(), (nameof(place), place), (nameof(force), force));
        internal static string Done(object? place, object? force) => Format(T(), (nameof(place), place), (nameof(force), force));
        internal static string KeepValues => T();
        internal static string NoTarget => T();
        internal static string HasForce(object? place, object? force) => Format(T(), (nameof(place), place), (nameof(force), force));
        internal static string PauseFirst => T();
        internal static string NotRun(object? why) => Format(T(), (nameof(why), why));
        internal static string Unconfirmed(object? place) => Format(T(), (nameof(place), place));
        internal static string Gone => T();
        internal static string Taken => T();
        internal static string NoRuler(object? force) => Format(T(), (nameof(force), force));
    }

    // 변경 적용
    internal static class Apply
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Apply) + "." + key);

        internal static string DiscardAndGoOn => T();
        internal static string Writing => T();
        internal static string WritingWhileRunning => T();
        internal static string Written(object? count) => Format(T(), (nameof(count), count));
        internal static string WrittenButChanged => T();
        internal static string AboveCap(object? field, object? value, object? cap) => Format(T(), (nameof(field), field), (nameof(value), value), (nameof(cap), cap));
        internal static string BreaksTies(object? broken) => Format(T(), (nameof(broken), broken));
        internal static string Request(object? record, object? changes) => Format(T(), (nameof(record), record), (nameof(changes), changes));
        internal static string WroteOverGame(object? change) => Format(T(), (nameof(change), change));
        internal static string GameChangedBack(object? change) => Format(T(), (nameof(change), change));
        internal static string ItemNotCounted(object? item, object? why) => Format(T(), (nameof(item), item), (nameof(why), why));
        internal static string ItemStillListed(object? item, object? why) => Format(T(), (nameof(item), item), (nameof(why), why));
        internal static string LinkedMoved(object? message) => Format(T(), (nameof(message), message));
    }

    // 되돌리기
    internal static class Undo
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Undo) + "." + key);

        internal static string Missing(object? record) => Format(T(), (nameof(record), record));
        internal static string Ask(object? summary) => Format(T(), (nameof(summary), summary));
        internal static string NothingTip => T();
        internal static string LastTip(object? count, object? summary) => Format(T(), (nameof(count), count), (nameof(summary), summary));
        internal static string StoppedLinked(object? changes) => Format(T(), (nameof(changes), changes));
        internal static string LinkedMoved(object? changes, object? record) => Format(T(), (nameof(changes), changes), (nameof(record), record));
        internal static string AndMore(object? count) => Format(T(), (nameof(count), count));
        internal static string Dropped(object? summary) => Format(T(), (nameof(summary), summary));
        internal static string Moved(object? changes) => Format(T(), (nameof(changes), changes));
        internal static string Undoing(object? summary) => Format(T(), (nameof(summary), summary));
    }

    // 확인 후 바로 쓰는 일괄 버튼
    internal static class Fills
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Fills) + "." + key);

        internal static string HealUnits(object? force) => Format(T(), (nameof(force), force));
        internal static string RaiseMorale(object? force, object? morale) => Format(T(), (nameof(force), force), (nameof(morale), morale));
        internal static string NothingToChange(object? what) => Format(T(), (nameof(what), what));
        internal static string AndMore(object? count) => Format(T(), (nameof(count), count));
        internal static string WritesTo(object? what, object? count, object? names) => Format(T(), (nameof(what), what), (nameof(count), count), (nameof(names), names));
        internal static string Loyalty150(object? who) => Format(T(), (nameof(who), who));
    }

    // 목록 창
    internal static class Lists
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Lists) + "." + key);

        internal static string SearchForces => T();
        internal static string SearchCities => T();
        internal static string SearchOfficers => T();
        internal static string SearchUnits => T();
        internal static string SearchPersonalities => T();
        internal static string SearchItems => T();
        internal static string SearchAncients => T();
        internal static string ForcesTab => T();
        internal static string CitiesTab => T();
        internal static string OfficersTab => T();
        internal static string UnitsTab => T();
        internal static string PersonalitiesTab => T();
        internal static string ItemsTab => T();
        internal static string AncientTab => T();
        internal static string ActiveOfficers => T();
        internal static string AllOfficers => T();
        internal static string ArmyUnits => T();
        internal static string AllUnits => T();
        internal static string AllForces => T();
        internal static string AllAncients => T();
        internal static string LockedAncients => T();
        internal static string ConnectToSee => T();
        internal static string ArtFound(object? count) => Format(T(), (nameof(count), count));
        internal static string CloseTip => T();
        internal static string EditorTab => T();
        internal static string SelectEditor => T();
        internal static string EditCellTip => T();
        internal static string DraftCellTip => T();
        internal static string DraftApplyNote => T();
        internal static string DraftScope => T();
        internal static string DraftApplyTip => T();
        internal static string DraftColumnTip(object? field) => Format(T(), (nameof(field), field));
        internal static string BulkColumn(object? field) => Format(T(), (nameof(field), field));
        internal static string BulkColumnTip(object? field) => Format(T(), (nameof(field), field));
        internal static string BulkScope(object? count) => Format(T(), (nameof(count), count));
        internal static string BulkSkipped(object? count) => Format(T(), (nameof(count), count));
        internal static string BulkValue(object? field, object? value) => Format(T(), (nameof(field), field), (nameof(value), value));
        internal static string BulkUnavailable => T();
        internal static string ForceCount(object? count) => Format(T(), (nameof(count), count));
        internal static string CityCount(object? count) => Format(T(), (nameof(count), count));
        internal static string OfficerCount(object? count) => Format(T(), (nameof(count), count));
        internal static string UnitCount(object? count) => Format(T(), (nameof(count), count));
        internal static string PersonalityCount(object? count) => Format(T(), (nameof(count), count));
        internal static string ItemCount(object? count) => Format(T(), (nameof(count), count));
        internal static string AncientCount(object? count, object? locked) => Format(T(), (nameof(count), count), (nameof(locked), locked));
        internal static string PortraitsFailed(object? message) => Format(T(), (nameof(message), message));
    }

    // 표의 열 제목
    internal static class Columns
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Columns) + "." + key);

        internal static string County => T();
        internal static string Base => T();
        internal static string Levels => T();
        internal static string Force => T();
        internal static string Ruler => T();
        internal static string Rank => T();
        internal static string Cities => T();
        internal static string Officers => T();
        internal static string Troops => T();
        internal static string City => T();
        internal static string Scale => T();
        internal static string Governor => T();
        internal static string Gold => T();
        internal static string Food => T();
        internal static string NameCourtesy => T();
        internal static string ForceBase => T();
        internal static string Status => T();
        internal static string Commander => T();
        internal static string Kind => T();
        internal static string Location => T();
        internal static string Personality => T();
        internal static string Does => T();
        internal static string Value1 => T();
        internal static string Value2 => T();
        internal static string Holders => T();
        internal static string Item => T();
        internal static string Ability => T();
        internal static string Owner => T();
        internal static string State => T();
        internal static string Gate => T();
        internal static string Morale => T();
        internal static string Durability => T();
        internal static string Unlock => T();
    }

    // 편집 창 공통
    internal static class Card
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Card) + "." + key);

        internal static string RevertInput => T();
        internal static string RevertInputTip => T();
        internal static string BackTip => T();
        internal static string ForwardTip => T();
        internal static string Apply => T();
        internal static string ApplyTip => T();
        internal static string CloseTip => T();
        internal static string RemoveTip => T();
        internal static string GameValue(object? value) => Format(T(), (nameof(value), value));
        internal static string UnnamedNumberTip => T();
        internal static string GoToTip => T();
        internal static string WentTo(object? place) => Format(T(), (nameof(place), place));
        internal static string NotWent(object? why) => Format(T(), (nameof(why), why));
        internal static string NoPlace => T();
    }

    // 세력 편집 창
    internal static class Force
    {
        internal static string MilitaryPage => T();
        internal static string MilitaryPoints => T();
        internal static string MilitaryHint => T();
        internal static string MilitaryUnavailable => T();
        internal static string MilitaryCorps(object? number, object? leader) => Format(T(), (nameof(number), number), (nameof(leader), leader));
        internal static string MilitaryLimit(object? maximum) => Format(T(), (nameof(maximum), maximum));
        internal static string MilitaryRange(object? maximum) => Format(T(), (nameof(maximum), maximum));
        private static string T([CallerMemberName] string key = "") => Get(nameof(Force) + "." + key);

        internal static string Summary(object? ruler, object? cities, object? officers, object? troops) => Format(T(), (nameof(ruler), ruler), (nameof(cities), cities), (nameof(officers), officers), (nameof(troops), troops));
        internal static string SummaryWithGates(object? ruler, object? cities, object? gates, object? officers, object? troops) => Format(T(), (nameof(ruler), ruler), (nameof(cities), cities), (nameof(gates), gates), (nameof(officers), officers), (nameof(troops), troops));
        internal static string PolicyHolderTip(object? description, object? level) => Format(T(), (nameof(description), description), (nameof(level), level));
        internal static string RulerLabel => T();
        internal static string StrategistLabel => T();
        internal static string AlwaysInEffect(object? holders) => Format(T(), (nameof(holders), holders));
        internal static string StrategistChoiceTip(object? leadership, object? politics) => Format(T(), (nameof(leadership), leadership), (nameof(politics), politics));
        internal static string NoCities => T();
        internal static string NoOfficers => T();
        internal static string Heal => T();
        internal static string HealTip => T();
        internal static string Morale(object? morale) => Format(T(), (nameof(morale), morale));
        internal static string MoraleTip(object? morale) => Format(T(), (nameof(morale), morale));
        internal static string BasicPage => T();
        internal static string PolicyPage => T();
        internal static string CityPage => T();
        internal static string OfficerPage => T();
        internal static string UnitPage => T();
        internal static string Country => T();
        internal static string CountryTip => T();
        internal static string Rank => T();
        internal static string Strategist => T();
        internal static string StrategistTip => T();
        internal static string ColourTip(object? number) => Format(T(), (nameof(number), number));
        internal static string Colour => T();
        internal static string Relations => T();
        internal static string AllFriends => T();
        internal static string AllFriendsTip => T();
        internal static string NoFriends => T();
        internal static string NoFriendsTip => T();
        internal static string RelationsNote => T();
        internal static string Hurry => T();
        internal static string HurryTip => T();
        internal static string PolicyNote => T();
        internal static string UnitCount(object? count) => Format(T(), (nameof(count), count));
        internal static string NoUnits => T();
        internal static string AllianceMonths => T();
        internal static string AllianceTip(object? max) => Format(T(), (nameof(max), max));
        internal static string AllianceError(object? force, object? max) => Format(T(), (nameof(force), force), (nameof(max), max));
        internal static string AllianceUnavailableLabel => T();
        internal static string AllianceUnavailable => T();
        internal static string AlliancePlayerUnknown => T();
        internal static string AllianceNotRun(object? why) => Format(T(), (nameof(why), why));
        internal static string AllianceSynced(object? first, object? second, object? months) => Format(T(), (nameof(first), first), (nameof(second), second), (nameof(months), months));
        internal static string FriendshipError(object? force, object? max) => Format(T(), (nameof(force), force), (nameof(max), max));
        internal static string FriendshipTip(object? max) => Format(T(), (nameof(max), max));
        internal static string AddPolicyHolderTip(object? field) => Format(T(), (nameof(field), field));
        internal static string PolicyDays(object? days) => Format(T(), (nameof(days), days));
        internal static string PolicyDaysTip(object? days) => Format(T(), (nameof(days), days));
        internal static string PolicyInEffectTip => T();
    }

    // 도시 편집 창
    internal static class City
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(City) + "." + key);

        internal static string NoOfficers => T();
        internal static string CityPage => T();
        internal static string CountyPage => T();
        internal static string OfficersPage => T();
        internal static string Governor => T();
        internal static string GovernorTip => T();
        internal static string FillCaps => T();
        internal static string FillCapsTip => T();
        internal static string LevelsNote(object? steps, object? levels) => Format(T(), (nameof(steps), steps), (nameof(levels), levels));
        internal static string BulkRow => T();
        internal static string BulkTip(object? field, object? min, object? max) => Format(T(), (nameof(field), field), (nameof(min), min), (nameof(max), max));
        internal static string BulkApply => T();
        internal static string BulkApplyTip => T();
        internal static string GovernedByLord => T();
        internal static string OccupiedBy(object? occupier) => Format(T(), (nameof(occupier), occupier));
        internal static string ScaleOf(object? scale) => Format(T(), (nameof(scale), scale));
        internal static string GovernorLabel => T();
        internal static string CountyCount(object? count) => Format(T(), (nameof(count), count));
        internal static string CapsByScale(object? caps) => Format(T(), (nameof(caps), caps));
    }

    // 관문 편집 창
    internal static class Gate
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Gate) + "." + key);

        internal static string GatePage => T();
        internal static string GovernorTip => T();
        internal static string GovernorChoiceTip(object? leadership, object? strength) => Format(T(), (nameof(leadership), leadership), (nameof(strength), strength));
        internal static string Mend => T();
        internal static string MendTip => T();
        internal static string GuardedByLord => T();
        internal static string Kind => T();
    }

    // Facility editor
    internal static class Facility
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Facility) + "." + key);

        internal static string Page => T();
        internal static string Number(object? index) => Format(T(), (nameof(index), index));
        internal static string DurabilityHint => T();
        internal static string Gone => T();
        internal static string Closed(object? name) => Format(T(), (nameof(name), name));
        internal static string ClosedDropped(object? name) => Format(T(), (nameof(name), name));
    }

    // 장수 편집 창
    internal static class Officer
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Officer) + "." + key);

        internal static string OnCampaign(object? place) => Format(T(), (nameof(place), place));
        internal static string InBase => T();
        internal static string OutsideBase => T();
        internal static string CorpsNumber(object? corps) => Format(T(), (nameof(corps), corps));
        internal static string CorpsLeader(object? leader) => Format(T(), (nameof(leader), leader));
        internal static string NotServingMonths(object? months) => Format(T(), (nameof(months), months));
        internal static string OtherForce => T();
        internal static string Move => T();
        internal static string Send => T();
        internal static string Dismiss => T();
        internal static string Likes => T();
        internal static string Dislikes => T();
        internal static string BasicPage => T();
        internal static string TraitPage => T();
        internal static string TiesPage => T();
        internal static string StandingPage => T();
        internal static string Loyalty => T();
        internal static string Years => T();
        internal static string Debut => T();
        internal static string Death => T();
        internal static string Traits => T();
        internal static string Gender => T();
        internal static string Ideology => T();
        internal static string Policy => T();
        internal static string PolicyLevel => T();
        internal static string Office => T();
        internal static string Compatibility => T();
        internal static string PolicyLevelTip => T();
        internal static string GenderTip => T();
        internal static string ExperienceNote => T();
        internal static string Personalities => T();
        internal static string Tactics => T();
        internal static string UniqueTactic => T();
        internal static string Formations => T();
        internal static string Ties => T();
        internal static string Spouse => T();
        internal static string SpouseTip => T();
        internal static string Sworn => T();
        internal static string SwornTip => T();
        internal static string Items => T();
        internal static string Give => T();
        internal static string GiveTip => T();
        internal static string Now => T();
        internal static string Force => T();
        internal static string Corps => T();
        internal static string Base => T();
        internal static string Place => T();
        internal static string Status => T();
        internal static string FormerLord => T();
        internal static string MoveHeading => T();
        internal static string SameForce => T();
        internal static string MoveTip => T();
        internal static string TargetBase => T();
        internal static string TransferNote => T();
        internal static string RecruitingForce => T();
        internal static string Recruit => T();
        internal static string Release => T();
        internal static string FreeNote(object? loyalty) => Format(T(), (nameof(loyalty), loyalty));
        internal static string PrisonerNote(object? months) => Format(T(), (nameof(months), months));
        internal static string HiddenNote => T();
        internal static string DeadNote => T();
        internal static string ServingNote(object? loyalty, object? months) => Format(T(), (nameof(loyalty), loyalty), (nameof(months), months));
        internal static string Male => T();
        internal static string Female => T();
        internal static string OfficeOnlyVassals => T();
        internal static string TacticNumber(object? id) => Format(T(), (nameof(id), id));
        internal static string SwornGroup => T();
        internal static string OfficeOf(object? office) => Format(T(), (nameof(office), office));
        internal static string TakenFrom(object? holder) => Format(T(), (nameof(holder), holder));
        internal static string GivenFromNone => T();
        internal static string PickListedOfficer => T();
        internal static string OfficeTaken(object? holder) => Format(T(), (nameof(holder), holder));
        internal static string TacticsFull(object? max) => Format(T(), (nameof(max), max));
        internal static string WillLearn => T();
        internal static string WillForget => T();
        internal static string WillForgetForGood => T();
        internal static string KeptTacticTip => T();
        internal static string TacticCount(object? learned, object? max) => Format(T(), (nameof(learned), learned), (nameof(max), max));
        internal static string AddTieTip(object? tie, object? max) => Format(T(), (nameof(tie), tie), (nameof(max), max));
        internal static string NewTie(object? tie) => Format(T(), (nameof(tie), tie));
        internal static string Loyalty150 => T();
        internal static string Loyalty150Tip(object? status) => Format(T(), (nameof(status), status));
        internal static string GroupCount(object? group, object? count) => Format(T(), (nameof(group), group), (nameof(count), count));
    }

    // 장수 옮기기·등용·내보내기
    internal static class Transfers
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Transfers) + "." + key);

        internal static string RulerStays => T();
        internal static string RulerCityOnly => T();
        internal static string RulerMoves => T();
        internal static string CorpsLeaderMoves => T();
        internal static string CorpsLeaderReleased => T();
        internal static string UnitReturns => T();
        internal static string NativeUndoNote => T();
        internal static string RouteTableMissing => T();
        internal static string PauseRequired => T();
        internal static string ReadAccessDenied => T();
        internal static string StateChanged => T();
        internal static string DestinationMissing => T();
        internal static string DestinationInvalid => T();
        internal static string DestinationChanged => T();
        internal static string MultipleUnits => T();
        internal static string UnitBusy(object? officer) => Format(T(), (nameof(officer), officer));
        internal static string NoRoute(object? officer, object? home) => Format(T(), (nameof(officer), officer), (nameof(home), home));
        internal static string RouteEvidence(object? officer, object? unit, object? from, object? to, object? length, object? force) => Format(T(), (nameof(officer), officer), (nameof(unit), unit), (nameof(from), from), (nameof(to), to), (nameof(length), length), (nameof(force), force));
        internal static string CorpsSurvivalUnknown => T();
        internal static string SuccessionEvidence(object? corps) => Format(T(), (nameof(corps), corps));
        internal static string UnitListMissing => T();
        internal static string UnitListChanged => T();
        internal static string UnitListMismatch => T();
        internal static string ExecutionUnconfirmed(object? why) => Format(T(), (nameof(why), why));
        internal static string WriteStateChanged => T();
        internal static string AppliedOfficerEvidence(object? officer, object? corps, object? home, object? status) => Format(T(), (nameof(officer), officer), (nameof(corps), corps), (nameof(home), home), (nameof(status), status));
        internal static string AppliedUnitEvidence(object? unit, object? order, object? target, object? next, object? group) => Format(T(), (nameof(unit), unit), (nameof(order), order), (nameof(target), target), (nameof(next), next), (nameof(group), group));
        internal static string NativeCompleted => T();
        internal static string CorpsLeaderStays => T();
        internal static string OnlyVassals => T();
        internal static string OnCampaign => T();
        internal static string OutsideBase => T();
        internal static string RecruitOnly => T();
        internal static string ReleaseOnly => T();
        internal static string NoDate => T();
        internal static string PickBase => T();
        internal static string SameForceOnly => T();
        internal static string OtherForceOnly => T();
        internal static string MoveSummary(object? officer, object? origin, object? to) => Format(T(), (nameof(officer), officer), (nameof(origin), origin), (nameof(to), to));
        internal static string DefectSummary(object? officer, object? origin, object? force, object? to, object? loyalty) => Format(T(), (nameof(officer), officer), (nameof(origin), origin), (nameof(force), force), (nameof(to), to), (nameof(loyalty), loyalty));
        internal static string DismissSummary(object? officer, object? origin, object? home, object? months) => Format(T(), (nameof(officer), officer), (nameof(origin), origin), (nameof(home), home), (nameof(months), months));
        internal static string RecruitSummary(object? officer, object? status, object? force, object? to, object? loyalty) => Format(T(), (nameof(officer), officer), (nameof(status), status), (nameof(force), force), (nameof(to), to), (nameof(loyalty), loyalty));
        internal static string ReleaseSummary(object? officer, object? home) => Format(T(), (nameof(officer), officer), (nameof(home), home));
        internal static string NoGovernor(object? home) => Format(T(), (nameof(home), home));
        internal static string WithNotes(object? summary, object? notes) => Format(T(), (nameof(summary), summary), (nameof(notes), notes));
        internal static string NoStrategist(object? force) => Format(T(), (nameof(force), force));
        internal static string LeftPolicySlot(object? force, object? field) => Format(T(), (nameof(force), force), (nameof(field), field));
    }

    // 부대 편집 창
    internal static class Unit
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Unit) + "." + key);

        internal static string ComeAndGo => T();
        internal static string Place => T();
        internal static string Destination => T();
        internal static string Heading => T();
        internal static string Heal => T();
        internal static string HealTip => T();
        internal static string Morale(object? morale) => Format(T(), (nameof(morale), morale));
        internal static string MoraleTip(object? morale) => Format(T(), (nameof(morale), morale));
        internal static string Page => T();
        internal static string Title(object? commander) => Format(T(), (nameof(commander), commander));
        internal static string CommanderLabel => T();
        internal static string Gone(object? commander) => Format(T(), (nameof(commander), commander));
        internal static string GoneDropped(object? commander) => Format(T(), (nameof(commander), commander));
        internal static string StatusPage => T();
        internal static string Boosts => T();
        internal static string Ailments => T();
        internal static string Level => T();
        internal static string TimeLeft => T();
        internal static string BoostAll => T();
        internal static string BoostAllTip(object? level, object? time) => Format(T(), (nameof(level), level), (nameof(time), time));
        internal static string ClearAilments => T();
        internal static string ClearAilmentsTip => T();
        internal static string TimeNote(object? hours, object? percent) => Format(T(), (nameof(hours), hours), (nameof(percent), percent));
        internal static string CurrentConditions => T();
        internal static string ConditionsNote => T();
        internal static string ConditionUnknown => T();
        internal static string Poison => T();
        internal static string PoisonDamage => T();
        internal static string PoisonImmune => T();
        internal static string PoisonUnknown => T();
        internal static string PoisonHint => T();
        internal static string Fire => T();
        internal static string FireDamage => T();
        internal static string FireImmune => T();
        internal static string FireUnknown => T();
        internal static string FireHint => T();
        internal static string Supply => T();
        internal static string SupplyConnected => T();
        internal static string SupplyCut => T();
        internal static string SupplyHint => T();
    }

    // 개성 편집 창
    internal static class Personality
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Personality) + "." + key);

        internal static string Does => T();
        internal static string Effects => T();
        internal static string Effect1 => T();
        internal static string Effect2 => T();
        internal static string Value => T();
        internal static string Holders => T();
        internal static string Page => T();
        internal static string Subtitle(object? count, object? active) => Format(T(), (nameof(count), count), (nameof(active), active));
        internal static string SwitchOnly => T();
        internal static string Condition => T();
        internal static string ConditionNumber(object? condition) => Format(T(), (nameof(condition), condition));
        internal static string NoShipsOrSiege => T();
        internal static string TroopsCondition(object? troops) => Format(T(), (nameof(troops), troops));
        internal static string TroopsLabel => T();
        internal static string RangeLabel => T();
        internal static string Filter => T();
        internal static string Timing => T();
        internal static string TimingAlways => T();
        internal static string TimingWhileMet => T();
        internal static string TimingAfterAttack(object? days) => Format(T(), (nameof(days), days));
        internal static string TimingTip => T();
        internal static string NoHolders => T();
        internal static string MoreHolders(object? count) => Format(T(), (nameof(count), count));
        internal static string Note => T();
    }

    // 개성 조건·효과가 작동하는지 (게임 코드에서 읽은 규칙)
    internal static class PersonalityRules
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(PersonalityRules) + "." + key);

        internal static string UnusedCondition => T();
        internal static string AfterAttackKeepsCondition => T();
        internal static string AttackedNeedsDays => T();
        internal static string AlwaysTakesNoCondition => T();
        internal static string EventTakesNoKind => T();
        internal static string EventOnly => T();
        internal static string NoReader => T();
        internal static string NeedsLit => T();
        internal static string OthersOnly => T();
        internal static string OwnOnly => T();
        internal static string AlwaysOnly => T();
        internal static string NeedsCondition => T();
        internal static string GuardNeedsRange => T();
        internal static string ReachUnit => T();
        internal static string ReachReceived => T();
        internal static string ReachOwn => T();
        internal static string ReachOwnLit => T();
        internal static string ReachOfficer => T();
        internal static string ReachOfficerAlways => T();
        internal static string ReachGuard => T();
        internal static string ReachEvent => T();
    }

    // 명품 편집 창
    internal static class Item
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Item) + "." + key);

        internal static string NoOwner => T();
        internal static string Found => T();
        internal static string Hidden => T();
        internal static string StateNumber(object? state) => Format(T(), (nameof(state), state));
        internal static string AbilityHeading => T();
        internal static string Ability => T();
        internal static string Bonus => T();
        internal static string Page => T();
        internal static string Kind => T();
        internal static string Note => T();
    }

    // 고대무장 (목록 창)
    internal static class Ancient
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Ancient) + "." + key);

        internal static string Always => T();
        internal static string Unlocked => T();
        internal static string Locked => T();
        internal static string DlcHeld => T();
        internal static string DlcMissing => T();
        internal static string Dlc => T();
        internal static string WithDlc => T();
        internal static string Tutorial(object? number) => Format(T(), (nameof(number), number));
        internal static string Scenario(object? scenario) => Format(T(), (nameof(scenario), scenario));
        internal static string Chronicle(object? chronicle) => Format(T(), (nameof(chronicle), chronicle));
        internal static string ChronicleS => T();
        internal static string UnlockAll => T();
        internal static string UnlockAllTip => T();
        internal static string Label => T();
        internal static string UnlockSummary(object? count, object? names) => Format(T(), (nameof(count), count), (nameof(names), names));
        internal static string NothingLocked => T();
        internal static string NoPlayRecord => T();
        internal static string SaveInGame => T();
    }

    // 지도
    internal static class Map
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Map) + "." + key);

        internal static string ClickedPosition => T();
        internal static string TribeTip(object? name, object? force, object? troops, object? gold) => Format(T(), (nameof(name), name), (nameof(force), force), (nameof(troops), troops), (nameof(gold), gold));
        internal static string GateTip(object? name, object? force, object? troops, object? morale, object? durability) => Format(T(), (nameof(name), name), (nameof(force), force), (nameof(troops), troops), (nameof(morale), morale), (nameof(durability), durability));
        internal static string LandmarkTip(object? name) => Format(T(), (nameof(name), name));
        internal static string Territory(object? force) => Format(T(), (nameof(force), force));
        internal static string BuildingNote => T();
        internal static string DurabilityOf(object? durability, object? full) => Format(T(), (nameof(durability), durability), (nameof(full), full));
        internal static string Durability(object? durability) => Format(T(), (nameof(durability), durability));
        internal static string Building => T();
        internal static string UnitsAll => T();
        internal static string UnitsOfForce => T();
        internal static string UnitsHidden => T();
        internal static string UnitModesTip => T();
        internal static string Units => T();
        internal static string UnitCount(object? count) => Format(T(), (nameof(count), count));
        internal static string TransportMark => T();
        internal static string CityTip(object? name, object? force, object? scale) => Format(T(), (nameof(name), name), (nameof(force), force), (nameof(scale), scale));
        internal static string GovernorPart(object? governor) => Format(T(), (nameof(governor), governor));
        internal static string CityStores(object? troops, object? gold, object? food) => Format(T(), (nameof(troops), troops), (nameof(gold), gold), (nameof(food), food));
        internal static string UnitHeading(object? order, object? target, object? distance) => Format(T(), (nameof(order), order), (nameof(target), target), (nameof(distance), distance));
        internal static string UnitAgainst(object? order, object? force) => Format(T(), (nameof(order), order), (nameof(force), force));
        internal static string FormationPart(object? formation) => Format(T(), (nameof(formation), formation));
        internal static string UnitTip(object? commander, object? kind, object? formation) => Format(T(), (nameof(commander), commander), (nameof(kind), kind), (nameof(formation), formation));
        internal static string UnitForce(object? force) => Format(T(), (nameof(force), force));
        internal static string HomePart(object? home) => Format(T(), (nameof(home), home));
        internal static string PlacePart(object? place) => Format(T(), (nameof(place), place));
        internal static string UnitStrength(object? troops, object? wounded, object? morale) => Format(T(), (nameof(troops), troops), (nameof(wounded), wounded), (nameof(morale), morale));
        internal static string Date(object? year, object? month, object? day) => Format(T(), (nameof(year), year), (nameof(month), month), (nameof(day), day));
        internal static string Paused => T();
        internal static string Running => T();
        internal static string ConnectToSee => T();
        internal static string ForceChipOpenTip => T();
        internal static string ForceChipTip => T();
        internal static string Stacked(object? count, object? names) => Format(T(), (nameof(count), count), (nameof(names), names));
        internal static string StackedMore(object? count, object? names) => Format(T(), (nameof(count), count), (nameof(names), names));
    }

    // 게임 값의 이름과 도움말
    internal static class Fields
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Fields) + "." + key);

        internal static string RangeError(object? label, object? min, object? max) => Format(T(), (nameof(label), label), (nameof(min), min), (nameof(max), max));
        internal static string Gold => T();
        internal static string Food => T();
        internal static string Troops => T();
        internal static string Wounded => T();
        internal static string WoundedHint => T();
        internal static string Morale => T();
        internal static string CityMoraleHint => T();
        internal static string Durability => T();
        internal static string DurabilityHint => T();
        internal static string BaseDurability => T();
        internal static string GateMoraleHint => T();
        internal static string GateDurabilityHint => T();
        internal static string GateBaseDurabilityHint => T();
        internal static string Scale => T();
        internal static string ScaleHint => T();
        internal static string Governor => T();
        internal static string Population => T();
        internal static string PopulationHint => T();
        internal static string PublicOrder => T();
        internal static string Commerce => T();
        internal static string DevelopmentHint => T();
        internal static string Agriculture => T();
        internal static string Barracks => T();
        internal static string Loyalty => T();
        internal static string LoyaltyHint => T();
        internal static string Merit => T();
        internal static string Leadership => T();
        internal static string AbilityHint => T();
        internal static string Strength => T();
        internal static string Intelligence => T();
        internal static string Politics => T();
        internal static string Charm => T();
        internal static string Birth => T();
        internal static string Debut => T();
        internal static string DebutHint => T();
        internal static string Death => T();
        internal static string DeathHint => T();
        internal static string Gender => T();
        internal static string GenderHint => T();
        internal static string Spouse => T();
        internal static string Sworn => T();
        internal static string Compatibility => T();
        internal static string CompatibilityHint => T();
        internal static string Formations => T();
        internal static string UniqueTactic => T();
        internal static string Ideology => T();
        internal static string Policy => T();
        internal static string PersonalitySlot(object? slot) => Format(T(), (nameof(slot), slot));
        internal static string PersonalitiesInEffect => T();
        internal static string PersonalityTimeLeft(object? slot) => Format(T(), (nameof(slot), slot));
        internal static string TacticSlot(object? slot) => Format(T(), (nameof(slot), slot));
        internal static string LikeSlot(object? slot) => Format(T(), (nameof(slot), slot));
        internal static string DislikeSlot(object? slot) => Format(T(), (nameof(slot), slot));
        internal static string Corps => T();
        internal static string Base => T();
        internal static string Place => T();
        internal static string Status => T();
        internal static string FormerLord => T();
        internal static string FormerLordMonths => T();
        internal static string PrisonerOf => T();
        internal static string Captivity => T();
        internal static string JoinedYear => T();
        internal static string Service => T();
        internal static string ServiceValue => T();
        internal static string Office => T();
        internal static string PolicyLevel => T();
        internal static string Country => T();
        internal static string ForceColor => T();
        internal static string Strategist => T();
        internal static string Friendship(object? force) => Format(T(), (nameof(force), force));
        internal static string Alliance(object? force) => Format(T(), (nameof(force), force));
        internal static string Rank => T();
        internal static string PolicySlot(object? field, object? slot) => Format(T(), (nameof(field), field), (nameof(slot), slot));
        internal static string PolicySlotDays(object? field, object? slot) => Format(T(), (nameof(field), field), (nameof(slot), slot));
        internal static string ItemOwner => T();
        internal static string ItemState => T();
        internal static string ItemAbility => T();
        internal static string ItemBonus => T();
        internal static string ItemBonusHint => T();
        internal static string Effect1 => T();
        internal static string Effect2 => T();
        internal static string Effect1Value => T();
        internal static string Effect2Value => T();
        internal static string PersonalityCondition => T();
        internal static string PersonalityConditionValue => T();
        internal static string PersonalityFilter => T();
        internal static string AncientUnlocks1 => T();
        internal static string AncientUnlocks2 => T();
        internal static string PlayRecordChanged => T();
        internal static string UnitTroopsHint => T();
        internal static string UnitMoraleHint => T();
        internal static string Formation => T();
        internal static string BoostLevel(object? boost) => Format(T(), (nameof(boost), boost));
        internal static string BoostLevelHint(object? percent) => Format(T(), (nameof(percent), percent));
        internal static string TimeLeft(object? name) => Format(T(), (nameof(name), name));
        internal static string TimeLeftHint(object? hours) => Format(T(), (nameof(hours), hours));
        internal static string CapsHint(object? caps) => Format(T(), (nameof(caps), caps));
        internal static string Experience(object? ability) => Format(T(), (nameof(ability), ability));
    }

    // 게임 용어와 이름 없는 번호
    internal static class Terms
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Terms) + "." + key);

        internal static string StatusRuler => T();
        internal static string StatusCorpsLeader => T();
        internal static string StatusGovernor => T();
        internal static string StatusRegular => T();
        internal static string StatusFree => T();
        internal static string StatusPrisoner => T();
        internal static string StatusHidden => T();
        internal static string StatusDead => T();
        internal static string StatusNumber(object? status) => Format(T(), (nameof(status), status));
        internal static string PolicyBattle => T();
        internal static string PolicySupport => T();
        internal static string PolicyScheme => T();
        internal static string PolicyDomestic => T();
        internal static string PolicyPersonnel => T();
        internal static string OrderTransport => T();
        internal static string OrderAttack => T();
        internal static string OrderReturn => T();
        internal static string OrderMove => T();
        internal static string UnitArmy => T();
        internal static string UnitTransport => T();
        internal static string UnitKindNumber(object? kind) => Format(T(), (nameof(kind), kind));
        internal static string OfficerNumber(object? id) => Format(T(), (nameof(id), id));
        internal static string NoForce => T();
        internal static string Tribes => T();
        internal static string ForceNumber(object? force) => Format(T(), (nameof(force), force));
        internal static string GradeAndCap(object? grade, object? cap) => Format(T(), (nameof(grade), grade), (nameof(cap), cap));
        internal static string RecordCounty => T();
        internal static string RecordOfficer => T();
        internal static string RecordMap => T();
        internal static string RecordBuilding => T();
        internal static string RecordUnit => T();
        internal static string RecordItem => T();
        internal static string EffectNumber(object? kind) => Format(T(), (nameof(kind), kind));
        internal static string None => T();
        internal static string UnitOf(object? commander) => Format(T(), (nameof(commander), commander));
        internal static string Facility => T();
        internal static string ScaleSmall => T();
        internal static string ScaleMedium => T();
        internal static string ScaleLarge => T();
        internal static string ScaleHuge => T();
        internal static string ForceOfRuler(object? ruler) => Format(T(), (nameof(ruler), ruler));
        internal static string PolicyNumber(object? id) => Format(T(), (nameof(id), id));
        internal static string BoostMobility => T();
        internal static string BoostAttack => T();
        internal static string BoostSiege => T();
        internal static string BoostDefense => T();
        internal static string BoostBreach => T();
        internal static string Confused => T();
        internal static string Blocked => T();
        internal static string Provoked => T();
        internal static string Mobility(object? mobility) => Format(T(), (nameof(mobility), mobility));
    }

    // 쓰기 계획과 기록
    internal static class Edits
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Edits) + "." + key);

        internal static string NotGovernor(object? officer, object? city) => Format(T(), (nameof(officer), officer), (nameof(city), city));
        internal static string Castle(object? city) => Format(T(), (nameof(city), city));
        internal static string GateGone => T();
        internal static string NotGateGovernor(object? officer, object? gate) => Format(T(), (nameof(officer), officer), (nameof(gate), gate));
        internal static string GateBuilding(object? gate) => Format(T(), (nameof(gate), gate));
        internal static string ForceGone => T();
        internal static string UnitGone => T();
        internal static string NotLandFormation => T();
        internal static string TransportFormation => T();
        internal static string BoostWithoutTime(object? boost) => Format(T(), (nameof(boost), boost));
        internal static string ProvokeOnlyLowered => T();
        internal static string Overwritten(object? record, object? field, object? read, object? before, object? value) => Format(T(), (nameof(record), record), (nameof(field), field), (nameof(read), read), (nameof(before), before), (nameof(value), value));
        internal static string OfficerGone => T();
        internal static string ItemLabel(object? name) => Format(T(), (nameof(name), name));
        internal static string PersonalityGone => T();
        internal static string NotOnPersonalityCard(object? field) => Format(T(), (nameof(field), field));
        internal static string NoEffect(object? personality, object? field) => Format(T(), (nameof(personality), personality), (nameof(field), field));
        internal static string SwitchOnly(object? personality, object? field) => Format(T(), (nameof(personality), personality), (nameof(field), field));
        internal static string PersonalityCondition(object? personality, object? reason) => Format(T(), (nameof(personality), personality), (nameof(reason), reason));
        internal static string PersonalityKind(object? personality, object? field, object? reason) => Format(T(), (nameof(personality), personality), (nameof(field), field), (nameof(reason), reason));
        internal static string PersonalityFilter(object? personality) => Format(T(), (nameof(personality), personality));
        internal static string PersonalityNumber(object? personality, object? field, object? min, object? max) => Format(T(), (nameof(personality), personality), (nameof(field), field), (nameof(min), min), (nameof(max), max));
        internal static string PersonalityLabel(object? name) => Format(T(), (nameof(name), name));
        internal static string PersonalitySummary(object? personality, object? changes, object? count, object? active) => Format(T(), (nameof(personality), personality), (nameof(changes), changes), (nameof(count), count), (nameof(active), active));
        internal static string ItemGone => T();
        internal static string ItemCardOnly => T();
        internal static string MovedAfterWrite(object? record, object? field, object? written, object? now) => Format(T(), (nameof(record), record), (nameof(field), field), (nameof(written), written), (nameof(now), now));
        internal static string MovedSinceWrite(object? record, object? field, object? written, object? now, object? before) => Format(T(), (nameof(record), record), (nameof(field), field), (nameof(written), written), (nameof(now), now), (nameof(before), before));
    }

    // 자체 검사
    internal static class Checks
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Checks) + "." + key);

        internal static string GroupCity => T();
        internal static string GroupCounty => T();
        internal static string GroupAbilities => T();
        internal static string GroupYears => T();
        internal static string GroupTraits => T();
        internal static string GroupTies => T();
        internal static string GroupStanding => T();
        internal static string GroupForce => T();
        internal static string GroupItem => T();
        internal static string GroupUnit => T();
        internal static string GroupWorld => T();
        internal static string GroupPersonality => T();
        internal static string GroupPlayRecord => T();
        internal static string CityScale => T();
        internal static string CityStores => T();
        internal static string CityGovernor => T();
        internal static string GateGovernor => T();
        internal static string CastleBuilding => T();
        internal static string CountyRanges => T();
        internal static string AbilityExperience => T();
        internal static string YearOrder => T();
        internal static string TraitRanges => T();
        internal static string OneOfficeHolder => T();
        internal static string OfficeOnlyVassals => T();
        internal static string PersonalityIds => T();
        internal static string TacticIds => T();
        internal static string SpousesMatch => T();
        internal static string SwornGroups => T();
        internal static string TiesAreOfficers => T();
        internal static string MembersCorpsBase => T();
        internal static string Ruler => T();
        internal static string FreeHaveNoCorps => T();
        internal static string InsideBase => T();
        internal static string OwnFriendship => T();
        internal static string ForceRanges => T();
        internal static string Strategist => T();
        internal static string PolicySlots => T();
        internal static string FoundItemOwner => T();
        internal static string ItemAbility => T();
        internal static string PersonalityEffects => T();
        internal static string PlayRecordRanges => T();
        internal static string Units => T();
        internal static string GameDate => T();
        internal static string MissesChanged(object? fact, object? before, object? after) => Format(T(), (nameof(fact), fact), (nameof(before), before), (nameof(after), after));
        internal static string GameRunning => T();
        internal static string ReadOnly => T();
        internal static string Failed(object? facts) => Format(T(), (nameof(facts), facts));
    }

    // 오류
    internal static class Errors
    {
        private static string T([CallerMemberName] string key = "") => Get(nameof(Errors) + "." + key);

        internal static string ExeUnsupported => T();
        internal static string ExeTypeInfo(object? name) => Format(T(), (nameof(name), name));
        internal static string ExeVtables(object? names) => Format(T(), (nameof(names), names));
        internal static string ExeFormat => T();
        internal static string ExeTypeName(object? name) => Format(T(), (nameof(name), name));
        internal static string ExeRdata => T();
        internal static string ProcessChanged => T();
        internal static string NoWriteAccess => T();
        internal static string GameNotFound(object? exe) => Format(T(), (nameof(exe), exe));
        internal static string GameTwice(object? exe, object? count) => Format(T(), (nameof(exe), exe), (nameof(count), count));
        internal static string ExeInfo => T();
        internal static string DataNotFound => T();
        internal static string TablesNotFound(object? count) => Format(T(), (nameof(count), count));
        internal static string ProcessChangedReconnect => T();
        internal static string WriteMismatch(object? record, object? field, object? actual) => Format(T(), (nameof(record), record), (nameof(field), field), (nameof(actual), actual));
        internal static string NoStateForPlayer => T();
        internal static string StateMoved => T();
        internal static string ForceGone => T();
        internal static string AutoStopUnconfirmed => T();
        internal static string UnitChanged(object? unit) => Format(T(), (nameof(unit), unit));
        internal static string RollbackIncomplete => T();
        internal static string RollbackContextChanged(object? field) => Format(T(), (nameof(field), field));
        internal static string RollbackValueChanged => T();
        internal static string RollbackReadbackFailed => T();
        internal static string RollbackField(object? record, object? field, object? address, object? reason) =>
            Format(T(), (nameof(record), record), (nameof(field), field), (nameof(address), address), (nameof(reason), reason));
        internal static string RecordMismatch(object? record) => Format(T(), (nameof(record), record));
        internal static string CityMismatch => T();
        internal static string KindMismatch(object? what) => Format(T(), (nameof(what), what));
        internal static string CannotRead => T();
        internal static string ReadFailed => T();
        internal static string WriteFailed => T();
        internal static string NotZp => T();
        internal static string ZpBroken => T();
        internal static string RdbFormat(object? archive) => Format(T(), (nameof(archive), archive));
        internal static string ResourceFormat => T();
        internal static string TerrainSize => T();
    }
}
