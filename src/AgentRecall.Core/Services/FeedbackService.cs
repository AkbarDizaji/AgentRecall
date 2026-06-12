using System.Text;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;

namespace AgentRecall.Core.Services;

/// <summary>
/// Default <see cref="IFeedbackService"/>: records the raw feedback as a
/// <see cref="RecallEvent"/> and persists the extracted <see cref="RecallRule"/>
/// in <see cref="RuleStatus.Pending"/> state, linking the event to the rule.
/// </summary>
public sealed class FeedbackService : IFeedbackService
{
    private readonly IRecallEventRepository _events;
    private readonly IRecallRuleRepository _rules;
    private readonly IRecallExtractor _extractor;

    public FeedbackService(
        IRecallEventRepository events,
        IRecallRuleRepository rules,
        IRecallExtractor extractor)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public async Task<FeedbackResult> AddAsync(FeedbackInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Extract and persist the candidate rule first so the raw event can link to it.
        var rule = _extractor.Extract(input);
        rule.Status = RuleStatus.Pending;
        rule = await _rules.AddAsync(rule, cancellationToken).ConfigureAwait(false);

        var recallEvent = await _events.AddAsync(new RecallEvent
        {
            Type = RecallEventType.MistakeObserved,
            RuleId = rule.Id,
            Trigger = input.Task,
            Details = BuildDetails(input),
        }, cancellationToken).ConfigureAwait(false);

        return new FeedbackResult(recallEvent, rule);
    }

    private static string BuildDetails(FeedbackInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Feedback: {input.Feedback}");

        if (!string.IsNullOrWhiteSpace(input.BadOutput))
        {
            sb.AppendLine($"Bad output: {input.BadOutput}");
        }

        if (!string.IsNullOrWhiteSpace(input.FixedOutput))
        {
            sb.AppendLine($"Fixed output: {input.FixedOutput}");
        }

        if (!string.IsNullOrWhiteSpace(input.Tags))
        {
            sb.AppendLine($"Tags: {input.Tags}");
        }

        return sb.ToString().TrimEnd();
    }
}
