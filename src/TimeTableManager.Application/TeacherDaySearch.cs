using System.Collections.Immutable;
using System.Diagnostics;
using TimeTableManager.Domain;

namespace TimeTableManager.Application;

public enum TeacherDayGoal { Morning, Afternoon, OtherDates, ClearMorning }
public sealed record TeacherDayRequest(string TeacherId, DateOnly Date, TeacherDayGoal Goal,
    int MorningLastPeriod = 4, SwapDirection Direction = SwapDirection.SameDay, bool SpillOverflow = false);

/// <summary>Bounded beam over validated single-lesson chains. Only a complete, original-revision plan escapes.</summary>
public static class TeacherDaySearch
{
    private sealed record Branch(ProjectSession Session, int Remaining, int Changes, int ReviewCost, int Spread, int Distance);
    public static SwapSearchResult Find(ProjectSession owner, TeacherDayRequest request, IReadOnlySet<string> available,
        CancellationToken token, int maxBranches = 4, int maxAttempts = 1400, bool allowConsecutive = false,
        IReadOnlySet<string>? immovable = null, IReadOnlySet<string>? avoidConsecutiveTeachers = null)
    {
        token.ThrowIfCancellationRequested();
        if (owner.Document.Calendar is not { } calendar) return new(null, "날짜 설정을 확인하세요.", 0);
        var state = owner.Document.Current;
        var direction = request.Goal is (TeacherDayGoal.OtherDates or TeacherDayGoal.ClearMorning) || request.SpillOverflow ? request.Direction : SwapDirection.SameDay;
        var origin = state.Slots.FirstOrDefault(x => calendar.DateOf(x) == request.Date);
        if (origin is null) return new(null, "선택 날짜의 수업 칸이 없습니다.", 0);
        var slots = state.Slots.Where(x => available.Contains(x.Id) && x.Day <= 5 && CalendarSwapSearch.Allows(calendar, origin, x, direction)).ToArray();
        var days = slots.Select(calendar.DateOf).Where(x => x != request.Date).Distinct()
            .OrderBy(x => Math.Abs(x.DayNumber - request.Date.DayNumber)).ThenBy(x => x).ToArray();
        bool relocating = request.Goal is TeacherDayGoal.OtherDates or TeacherDayGoal.ClearMorning;
        // Day clearance prioritizes a complete valid placement. Consecutive teaching is reviewed by the user.
        allowConsecutive |= relocating;
        var slotMap = state.Slots.ToDictionary(x => x.Id);
        int toMove = state.Lessons.Count(x => x.TeacherId == request.TeacherId && calendar.DateOf(slotMap[x.SlotId]) == request.Date &&
            (request.Goal != TeacherDayGoal.ClearMorning || slotMap[x.SlotId].Period <= request.MorningLastPeriod));
        var scopes = new List<HashSet<string>>();
        void Add(IEnumerable<DateOnly> dates)
        {
            var selected = dates.Append(request.Date).ToHashSet();
            var scope = slots.Where(x => selected.Contains(calendar.DateOf(x))).Select(x => x.Id).ToHashSet();
            if (!scopes.Any(x => x.SetEquals(scope))) scopes.Add(scope);
        }
        if (relocating && toMove > 1 && days.Length > 1)
        {
            // Start with a small nearby window; distribution is a ranking preference, never a hard condition.
            Add(days.Take(2));
        }
        if (!relocating) Add([]);
        if (direction != SwapDirection.SameDay)
        {
            // Try a complete placement on the nearest one/two dates before widening the calendar span.
            foreach (var group in days.GroupBy(x => Math.Abs(x.DayNumber - request.Date.DayNumber)))
            {
                foreach (var date in group) Add([date]);
                Add(days.Where(x => Math.Abs(x.DayNumber - request.Date.DayNumber) <= group.Key));
            }
        }
        if (scopes.Count == 0) return new(null, "허용한 범위에 다른 평일이 없습니다. 작업 기간과 이전·이후 날짜 옵션을 확인하세요.", 0);
        int examined = 0;
        SwapSearchResult? firstFailure = null;
        var watch = Stopwatch.StartNew();
        foreach (var scope in scopes)
        {
            token.ThrowIfCancellationRequested();
            double remaining = 8000 - watch.Elapsed.TotalMilliseconds;
            if (remaining <= 0) break;
            var limit = TimeSpan.FromMilliseconds(Math.Min(remaining, scopes.Count == 1 ? 8000 : relocating && toMove >= 4 ? 2000 : 1000));
            var result = FindWithin(owner, request, scope, token, maxBranches, maxAttempts, allowConsecutive, limit, immovable, avoidConsecutiveTeachers);
            token.ThrowIfCancellationRequested();
            examined += result.Examined; firstFailure ??= result;
            if (scopes.Count == 1 && result.Plan is null) return result;
            if (result.Plan is not null) return result with { Examined = examined,
                Message = result.Message + (relocating ? " 배치 우선 · 연강 확인 후 수작업으로 조정하세요." : " 요청한 시간대와 가까운 날짜를 우선했습니다.") };
        }
        token.ThrowIfCancellationRequested();
        if (examined == 0 && firstFailure is not null) return firstFailure;
        return new(null, "제한 탐색에서 하루 수업 전체를 배치하지 못했습니다. 잠금·허용 날짜·시간대를 확인하세요. 실제 시간표는 변경되지 않았습니다.", examined);
    }
    private static SwapSearchResult FindWithin(ProjectSession owner, TeacherDayRequest request, IReadOnlySet<string> available,
        CancellationToken token, int maxBranches, int maxAttempts, bool allowConsecutive, TimeSpan limit,
        IReadOnlySet<string>? immovable, IReadOnlySet<string>? avoidConsecutiveTeachers)
    {
        token.ThrowIfCancellationRequested();
        if (maxBranches < 1 || maxAttempts < 1 || request.MorningLastPeriod < 1 ||
            !Enum.IsDefined(request.Goal) || !Enum.IsDefined(request.Direction))
            return new(null, "탐색 조건을 확인하세요.", 0);
        var document = owner.Document; var revision = owner.Revision;
        if (document.Calendar is not { } calendar) return new(null, "날짜 설정을 확인하세요.", 0);
        var original = document.Current; var slots = original.Slots.ToDictionary(x => x.Id);
        var consecutive = new ConsecutiveTeaching(original);
        bool relocating = request.Goal is TeacherDayGoal.OtherDates or TeacherDayGoal.ClearMorning;
        var dayLessons = original.Lessons.Where(x => x.TeacherId == request.TeacherId && calendar.DateOf(slots[x.SlotId]) == request.Date &&
            (request.Goal != TeacherDayGoal.ClearMorning || slots[x.SlotId].Period <= request.MorningLastPeriod)).ToArray();
        if (dayLessons.Length == 0) return new(null, request.Goal == TeacherDayGoal.ClearMorning ? "선택한 오전 시간대에 수업이 없습니다." : "선택한 교사·날짜에 수업이 없습니다.", 0);
        var origin = slots[dayLessons[0].SlotId];
        var exempt = relocating ? new HashSet<string>() : new HashSet<string>{request.TeacherId};
        bool AvoidRun(ConsecutiveRisk risk) => avoidConsecutiveTeachers?.Contains(risk.TeacherId) == true ||
            !allowConsecutive && !exempt.Contains(risk.TeacherId);
        if (dayLessons.Any(x => immovable?.Contains(x.Id) == true)) return new(null, "직접 선택한 하루 수업에 이동 미허용 수업이 있습니다. 요청을 다시 확인하세요.", 0);
        var daySlots = original.Slots.Where(x => available.Contains(x.Id) && calendar.DateOf(x) == request.Date).OrderBy(x => x.Period).ToArray();
        var lane = request.SpillOverflow
            ? daySlots.Where(x => request.Goal == TeacherDayGoal.Morning ? x.Period <= request.MorningLastPeriod : x.Period > request.MorningLastPeriod).ToArray()
            : daySlots;
        var packed = (request.Goal == TeacherDayGoal.Morning ? lane.Take(dayLessons.Length) : lane.TakeLast(dayLessons.Length)).Select(x => x.Id).ToHashSet();
        int requiredOnDay = relocating ? 0 : Math.Min(dayLessons.Length, lane.Length);
        bool Target(TimeSlot slot) => relocating
            ? calendar.DateOf(slot) != request.Date && CalendarSwapSearch.Allows(calendar, origin, slot, request.Direction)
            : packed.Contains(slot.Id) || request.SpillOverflow && calendar.DateOf(slot) != request.Date && CalendarSwapSearch.Allows(calendar, origin, slot, request.Direction);
        var direction = request.Goal is (TeacherDayGoal.OtherDates or TeacherDayGoal.ClearMorning) || request.SpillOverflow ? request.Direction : SwapDirection.SameDay;
        var scope = original.Slots.Where(x => available.Contains(x.Id) && CalendarSwapSearch.Allows(calendar, origin, x, direction))
            .Select(x => x.Id).ToHashSet();
        if (dayLessons.Any(x => !scope.Contains(x.SlotId))) return new(null, "선택한 날이 오늘 이전이거나 작업 기간 밖입니다.", 0);
        var goals = dayLessons.Select(x => x.Id).ToHashSet();
        var school = new SchoolDayRules(document);
        var blocked = dayLessons.Where(x => !Target(slots[x.SlotId]) && (x.Locked || school.IsExcluded(x))).ToArray();
        if (blocked.Length > 0) return new(null, $"옮겨야 할 수업 중 잠금·행사 제외가 {blocked.Length}개 있습니다. 일부만 옮기는 안은 만들지 않습니다.", 0);
        if (dayLessons.All(x => Target(slots[x.SlotId]))) return new(null, "이 날짜의 수업이 이미 모두 요청한 시간대에 있습니다.", 0);
        var targets = original.Slots.Where(x => scope.Contains(x.Id) && Target(x)).ToArray();
        var unchangedTeacherSlots = original.Lessons.Where(x => x.TeacherId == request.TeacherId && !goals.Contains(x.Id)).Select(x => x.SlotId).ToHashSet();
        var usableTargets = targets.Where(x => !unchangedTeacherSlots.Contains(x.Id)).ToArray();
        if (usableTargets.Length < dayLessons.Length)
            return new(null, "대상 시간대의 교시 수가 부족합니다. 오전·오후 경계 또는 날짜 범위를 바꾸세요. 일부 수업만 옮기지는 않습니다.", 0);
        if ((!allowConsecutive || avoidConsecutiveTeachers?.Contains(request.TeacherId) == true) && usableTargets.Length == dayLessons.Length)
        {
            // Every available period must be occupied; lesson order cannot avoid a forced run.
            var forced = original.Lessons.Where(x => !goals.Contains(x.Id))
                .Concat(dayLessons.Select((lesson, index) => lesson with { SlotId = usableTargets[index].Id }));
            if (consecutive.Evaluate(forced).Any(x => x.TeacherId == request.TeacherId && AvoidRun(x)))
                return new(null, "선택 시간대를 모두 채우면 3연강 이상이 생깁니다. 시간대를 넓히거나 연강 허용을 확인해야 합니다.", 0);
        }
        bool UnsafeFour(IEnumerable<LessonInstance> lessons) => !relocating && request.SpillOverflow &&
            consecutive.Evaluate(lessons).Any(x => x.TeacherId == request.TeacherId && x.Length >= 4 && calendar.DateOf(x.Day) != request.Date);
        int DestinationRun(ScheduleState state, LessonInstance lesson, TimeSlot target)
        {
            var periods = state.Lessons.Where(x => x.TeacherId == request.TeacherId && x.Id != lesson.Id && calendar.DateOf(slots[x.SlotId]) == calendar.DateOf(target))
                .Select(x => slots[x.SlotId].Period).Append(target.Period).ToHashSet();
            int first = target.Period, last = target.Period;
            while (periods.Contains(first - 1)) first--;
            while (periods.Contains(last + 1)) last++;
            return last - first + 1;
        }
        var before = original.Lessons.ToDictionary(x => x.Id);
        int examined = 0, attempts = 0;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(limit);
        ChangePlan Final(ProjectSession branch) => new(revision, ChangeKind.Move,
            $"교사 하루 수업 {(relocating ? (request.Goal == TeacherDayGoal.ClearMorning ? "오전 수업 비우기" : "다른 날짜 분산") : request.Goal == TeacherDayGoal.Morning ? "오전 모으기" : "오후 모으기")} · {request.Date:M/d}",
            branch.Document.Current.Lessons.Where(x => x != before[x.Id]).Select(x => new LessonDelta(before[x.Id], x)).ToImmutableArray());
        Branch Describe(ProjectSession branch)
        {
            var lessons = branch.Document.Current.Lessons;
            int remaining = lessons.Count(x => goals.Contains(x.Id) && !Target(slots[x.SlotId]));
            int spread = lessons.Where(x => x.TeacherId == request.TeacherId && scope.Contains(x.SlotId))
                .GroupBy(x => calendar.DateOf(slots[x.SlotId])).Sum(g => g.Count() * g.Count());
            int distance = lessons.Where(x => goals.Contains(x.Id)).Sum(x => Math.Abs(calendar.DateOf(slots[x.SlotId]).DayNumber - request.Date.DayNumber));
            int reviewCost = consecutive.Evaluate(lessons).Sum(x => x.Length * x.Length);
            return new(branch, remaining, Final(branch).Changes.Length, reviewCost, spread, distance);
        }
        var beam = new List<Branch> { Describe(new ProjectSession(document)) };
        Branch? bestBranch = null;
        SwapSearchResult? bestComplete = null;
        (int Changes, int Review, int Spread, int Distance) Score(Branch branch) => (branch.Changes, branch.ReviewCost, branch.Spread, branch.Distance);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string Key(ProjectSession branch) => string.Join("|", branch.Document.Current.Lessons.Select(x => x.SlotId.Length + ":" + x.SlotId));
        SwapSearchResult? Complete(ProjectSession branch)
        {
            var plan = Final(branch);
            if (Describe(branch).Remaining != 0 || plan.Changes.IsEmpty ||
                !plan.Changes.All(x => scope.Contains(x.Before.SlotId) && scope.Contains(x.After.SlotId)) || !owner.Preview(plan).IsValid ||
                plan.Changes.Any(x => immovable?.Contains(x.Before.Id) == true) ||
                (!allowConsecutive || avoidConsecutiveTeachers?.Count > 0) && consecutive.Evaluate(plan).Any(AvoidRun) || UnsafeFour(branch.Document.Current.Lessons)) return null;
            if (!relocating && branch.Document.Current.Lessons.Count(x => goals.Contains(x.Id) && calendar.DateOf(slots[x.SlotId]) == request.Date) != requiredOnDay) return null;
            // Existing lessons on other days are pinned, so none can fill the teacher's cleared day.
            if (relocating && branch.Document.Current.Lessons.Any(x => goals.Contains(x.Id) && calendar.DateOf(slots[x.SlotId]) == request.Date)) return null;
            return new(plan, $"선택 수업 {dayLessons.Length}개 배치 완료 · 함께 바뀌는 수업 {plan.Changes.Count(x => !goals.Contains(x.Before.Id))}개. " +
                (relocating ? "교체안과 연강 확인 목록을 검토한 뒤 적용하세요." : "다이어그램에서 확인한 뒤 적용하세요."), examined);
        }
        try
        {
            for (int depth = 0; depth < dayLessons.Length && beam.Count > 0 && attempts < maxAttempts; depth++)
            {
                var next = new List<Branch>();
                foreach (var branch in beam)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    var state = branch.Session.Document.Current;
                    var branchConsecutive = new ConsecutiveTeaching(state);
                    var remaining = state.Lessons.Where(x => goals.Contains(x.Id) && !Target(slots[x.SlotId]))
                        .OrderBy(x => slots[x.SlotId].Period).ToArray();
                    var pinned = state.Lessons.Where(x => x.TeacherId == request.TeacherId && (!goals.Contains(x.Id) || Target(slots[x.SlotId])))
                        .Select(x => x.Id).ToHashSet();
                    if (immovable is not null) pinned.UnionWith(immovable);
                    var occupied = state.Lessons.Where(x => x.TeacherId == request.TeacherId && pinned.Contains(x.Id)).Select(x => x.SlotId).ToHashSet();
                    // Alternate root order allows recovery when an early placement blocks a later lesson.
                    foreach (var lesson in remaining.Take(2))
                    {
                        int onDay = state.Lessons.Count(x => goals.Contains(x.Id) && packed.Contains(x.SlotId));
                        var ranked = targets.Where(x => !school.IsExcluded(lesson.ClassId, x.Id) && !occupied.Contains(x.Id) &&
                                (relocating || onDay >= requiredOnDay || calendar.DateOf(x) == request.Date))
                            .OrderBy(x => relocating ? state.Lessons.Count(l => l.Id != lesson.Id && l.SlotId == x.Id && (l.ClassId == lesson.ClassId || l.TeacherId == lesson.TeacherId || lesson.Room is not null && l.Room == lesson.Room)) : 0)
                            .ThenBy(x => relocating ? DestinationRun(state, lesson, x) : 0)
                            .ThenBy(x => relocating ? state.Lessons.Count(l => l.TeacherId == request.TeacherId && calendar.DateOf(slots[l.SlotId]) == calendar.DateOf(x)) : 0)
                            .ThenBy(x => Math.Abs(calendar.DateOf(x).DayNumber - request.Date.DayNumber))
                            .ThenBy(x => Math.Abs(x.Period - slots[lesson.SlotId].Period)).ThenBy(x => x.Period).ToArray();
                        int produced = 0;
                        foreach (bool chains in new[] { false, true })
                        {
                            foreach (var target in (chains ? ranked.Take(12) : ranked.Take(48)))
                            {
                                budget.Token.ThrowIfCancellationRequested();
                                if (++attempts > maxAttempts) break;
                                ChangePlan? step;
                                if (chains)
                                {
                                    using var slice = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                                    slice.CancelAfter(TimeSpan.FromMilliseconds(250));
                                    try
                                    {
                                        var found = SwapSearch.Find(branch.Session, lesson.Id, target.Id, scope, slice.Token,
                                            maxNodes: 80, maxChanged: Math.Max(6, dayLessons.Length + 4), immovable: pinned, allowConsecutive: allowConsecutive,
                                            consecutiveExemptTeachers: exempt, avoidConsecutiveTeachers: avoidConsecutiveTeachers);
                                        step = found.Plan; examined += found.Examined;
                                    }
                                    catch (OperationCanceledException) when (!budget.IsCancellationRequested) { continue; }
                                }
                                else
                                {
                                    step = ChangePlanner.Move(branch.Session, lesson.Id, target.Id); examined++;
                                    if (step.Changes.Any(x => pinned.Contains(x.Before.Id)) || !branch.Session.Preview(step).IsValid ||
                                        (!allowConsecutive || avoidConsecutiveTeachers?.Count > 0) && branchConsecutive.Evaluate(step).Any(AvoidRun)) continue;
                                }
                                if (step is null || !step.Changes.All(x => scope.Contains(x.Before.SlotId) && scope.Contains(x.After.SlotId))) continue;
                                var child = new ProjectSession(branch.Session.Document);
                                if (!child.Apply(step with { Revision = child.Revision }).IsValid || UnsafeFour(child.Document.Current.Lessons) || !seen.Add(Key(child))) continue;
                                var described = Describe(child);
                                if (described.Remaining >= branch.Remaining) continue;
                                if (Complete(child) is { } complete)
                                {
                                    if (bestBranch is null || Score(described).CompareTo(Score(bestBranch)) < 0)
                                    { bestBranch = described; bestComplete = complete; }
                                }
                                else next.Add(described);
                                produced++;
                                if (produced >= Math.Min(3, maxBranches)) break;
                            }
                            if (produced >= Math.Min(3, maxBranches) || attempts >= maxAttempts) break;
                        }
                        if (attempts >= maxAttempts) break;
                    }
                    if (attempts >= maxAttempts) break;
                }
                if (bestComplete is not null)
                { token.ThrowIfCancellationRequested(); return bestComplete with { Examined = examined }; }
                beam = next.OrderBy(x => x.Remaining).ThenBy(x => x.Changes).ThenBy(x => x.ReviewCost).ThenBy(x => x.Spread).ThenBy(x => x.Distance).Take(maxBranches).ToList();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        if (bestComplete is not null) return bestComplete with { Examined = examined };
        return new(null, "제한 탐색에서 하루 수업 전체를 배치하지 못했습니다. 잠금·행사 제외·중복 배정·허용 날짜를 확인하세요. 실제 시간표는 변경되지 않았습니다.", examined);
    }
}
