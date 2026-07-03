using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Seeds;

/// <summary>
/// The built-in <c>tidy-first</c> seed pack: small, high-signal, conditional rules for
/// separating behaviour-preserving cleanup from behaviour change. The guidance is original,
/// paraphrased practice inspired by common "tidy first" refactoring workflows; it contains
/// no copied source text.
/// </summary>
public static class TidyFirstSeedPack
{
    public const string Name = "tidy-first";

    public static SeedPackDefinition Definition { get; } = new()
    {
        Name = Name,
        Description = "Practical refactoring/tidying rules inspired by Tidy First-style workflows.",
        CopyrightNote =
            "Paraphrased practical guidance inspired by common tidying/refactoring practices. " +
            "Original conditional rules — no book text is copied or quoted.",
        Rules =
        [
            new SeedRuleDefinition
            {
                Key = "guard-clauses",
                Title = "Flatten nested conditionals with guard clauses",
                Trigger = "Nested if statements bury the main path and make the function hard to scan.",
                Action = "Prefer guard clauses or early returns to flatten the control flow, keeping behaviour identical.",
                AntiPattern = "Changing branch behaviour while flattening the structure.",
                Because = "Flattened control flow makes the next behaviour change easier to review and less error-prone.",
                Tags = "control-flow, guard-clause, readability",
            },
            new SeedRuleDefinition
            {
                Key = "separate-tidy-from-behavior",
                Title = "Separate tidying from behaviour changes",
                Trigger = "A change needs both structural cleanup and a behaviour modification.",
                Action = "Do the behaviour-preserving tidy as its own step, separate from the behaviour change, whenever possible.",
                AntiPattern = "Hiding a behaviour change inside a broad cleanup.",
                Because = "Reviewers can validate the safe tidy independently from the functional change.",
                Tags = "refactoring, reviewability",
            },
            new SeedRuleDefinition
            {
                Key = "name-repeated-condition",
                Title = "Name repeated or unclear conditions",
                Trigger = "The same condition repeats, or a boolean expression's meaning is not obvious.",
                Action = "Extract it into a named helper, local variable, or predicate before changing behaviour.",
                AntiPattern = "Duplicating complex boolean logic across branches.",
                Because = "A clear name makes the following behaviour change easier to understand and less likely to drift.",
                Tags = "conditionals, naming",
            },
            new SeedRuleDefinition
            {
                Key = "rename-before-logic",
                Title = "Rename unclear names before logic changes",
                Trigger = "Unclear naming makes an upcoming change risky to reason about.",
                Action = "Rename the variable, method, or concept first as a behaviour-preserving step, then make the behaviour change.",
                AntiPattern = "Combining a semantic rename with a behaviour change when it makes review harder.",
                Because = "Clear names reduce mistakes and make the intended behaviour change easier to verify.",
                Tags = "naming, reviewability",
            },
            new SeedRuleDefinition
            {
                Key = "extract-mixed-detail",
                Title = "Extract helpers when detail levels are mixed",
                Trigger = "A function mixes high-level workflow with low-level implementation detail.",
                Action = "Extract the low-level detail into a clearly named helper before changing the workflow.",
                AntiPattern = "Adding more branching or behaviour to a method that is already hard to scan.",
                Because = "Separating detail levels makes the important path easier to reason about.",
                Tags = "extraction, readability",
            },
            new SeedRuleDefinition
            {
                Key = "remove-obsolete-branches",
                Title = "Remove obsolete branches before adding new logic",
                Trigger = "Obsolete, unreachable, or redundant branches make the next change harder to reason about.",
                Action = "Remove or isolate them in a behaviour-preserving tidy before adding new logic.",
                AntiPattern = "Building new behaviour around dead paths.",
                Because = "Dead branches increase review burden and hide the real behaviour change.",
                Tags = "control-flow, cleanup",
            },
            new SeedRuleDefinition
            {
                Key = "split-risky-change",
                Title = "Split risky changes into tidy then behaviour",
                Trigger = "A planned change is large or risky.",
                Action = "First find a small, safe tidy that makes the behaviour change simpler, then implement the behaviour change separately.",
                AntiPattern = "Doing broad, opportunistic cleanup unrelated to the requested change.",
                Because = "Small, focused steps reduce regression risk and improve review quality.",
                Tags = "workflow, reviewability",
            },
            new SeedRuleDefinition
            {
                Key = "scope-the-tidy",
                Title = "Keep tidying scoped to the task",
                Trigger = "You are tidying before a behaviour change.",
                Action = "Stop once the code is clear enough to make the requested change safely.",
                AntiPattern = "Expanding a tidy into unrelated cleanup.",
                Because = "Unbounded cleanup increases risk and delays the actual task.",
                Tags = "scope-control, refactoring",
            },
            new SeedRuleDefinition
            {
                Key = "behavior-preserving",
                Title = "Keep tidy steps behaviour-preserving",
                Trigger = "You are applying a tidy step.",
                Action = "Preserve observable behaviour and avoid semantic changes unless the user explicitly asked for the behaviour change.",
                AntiPattern = "Mixing formatting, renaming, extraction, and semantic changes in one unclear step.",
                Because = "Behaviour-preserving changes are easier to trust, review, and revert.",
                Tags = "safety, refactoring",
            },
            new SeedRuleDefinition
            {
                Key = "make-structure-explicit",
                Title = "Make implicit structure explicit before changing it",
                Trigger = "Code has an implicit structure that makes the change hard to reason about.",
                Action = "Make that structure explicit with names, helpers, or clearer control flow before modifying behaviour.",
                AntiPattern = "Patching new behaviour into unclear structure when a small tidy would clarify it first.",
                Because = "Explicit structure helps the agent and reviewer see where the new behaviour belongs.",
                Tags = "structure, readability",
            },
        ],
    };
}
