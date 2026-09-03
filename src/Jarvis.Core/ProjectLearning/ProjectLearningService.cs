using Jarvis.Core.ProjectIntelligence;

namespace Jarvis.Core.ProjectLearning;

public sealed class ProjectLearningService : IProjectLearningService, IDisposable
{
    private const string TargetedFollowUpRationale =
        "The answer needs a more evidence-grounded explanation. Re-examine the repository evidence before concluding.";

    private static readonly InterviewDimension[] InterviewOrder =
    [
        InterviewDimension.ProjectOverview,
        InterviewDimension.Architecture,
        InterviewDimension.ActualImplementation,
        InterviewDimension.CSharpDotNet,
        InterviewDimension.ApiDesign,
        InterviewDimension.Database,
        InterviewDimension.Security,
        InterviewDimension.Testing,
        InterviewDimension.ErrorHandling,
        InterviewDimension.Performance,
        InterviewDimension.Concurrency,
        InterviewDimension.FailureScenarios,
        InterviewDimension.Scalability,
        InterviewDimension.DesignTradeOffs,
    ];

    private readonly IProjectLearningModel _model;
    private readonly IProjectLearningEvidenceSource _evidenceSource;
    private readonly IModelProfileRouter _profileRouter;
    private readonly IProjectLearningSessionStore _store;
    private readonly ProjectLearningConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public ProjectLearningService(
        IProjectLearningModel model,
        IProjectLearningEvidenceSource evidenceSource,
        IModelProfileRouter profileRouter,
        IProjectLearningSessionStore store,
        ProjectLearningConfiguration configuration,
        TimeProvider timeProvider)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _evidenceSource = evidenceSource ?? throw new ArgumentNullException(nameof(evidenceSource));
        _profileRouter = profileRouter ?? throw new ArgumentNullException(nameof(profileRouter));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ValidateConfiguration(configuration);
    }

    public ValueTask<ProjectLearningTurnResult> StartTutorAsync(
        string repositoryPath,
        TutorLevel level,
        string topic,
        bool askBeforeTell,
        ModelProfile requestedProfile,
        CancellationToken cancellationToken) => RunExclusiveAsync(
            token => StartTutorCoreAsync(
                repositoryPath,
                level,
                topic,
                askBeforeTell,
                requestedProfile,
                token),
            cancellationToken);

    public ValueTask<ProjectLearningTurnResult> ContinueTutorAsync(
        Guid sessionId,
        TutorInteractionKind interaction,
        string userInput,
        CancellationToken cancellationToken) => RunExclusiveAsync(
            token => ContinueTutorCoreAsync(sessionId, interaction, userInput, token),
            cancellationToken);

    public ValueTask<ProjectLearningTurnResult> StartInterviewAsync(
        string repositoryPath,
        InterviewDifficulty difficulty,
        int questionCount,
        ModelProfile requestedProfile,
        CancellationToken cancellationToken) => RunExclusiveAsync(
            token => StartInterviewCoreAsync(
                repositoryPath,
                difficulty,
                questionCount,
                requestedProfile,
                token),
            cancellationToken);

    public ValueTask<ProjectLearningTurnResult> SubmitInterviewAnswerAsync(
        Guid sessionId,
        string answer,
        CancellationToken cancellationToken) => RunExclusiveAsync(
            token => SubmitInterviewAnswerCoreAsync(sessionId, answer, token),
            cancellationToken);

    public ValueTask<ProjectLearningTurnResult> EndSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken) => RunExclusiveAsync(
            token => EndSessionCoreAsync(sessionId, token),
            cancellationToken);

    public ValueTask<ProjectLearningTurnResult> StartRevisionAsync(
        string repositoryPath,
        ModelProfile requestedProfile,
        CancellationToken cancellationToken) => RunExclusiveAsync(
            token => StartRevisionCoreAsync(repositoryPath, requestedProfile, token),
            cancellationToken);

    public void Dispose() => _operationGate.Dispose();

    private async ValueTask<ProjectLearningTurnResult> StartTutorCoreAsync(
        string repositoryPath,
        TutorLevel level,
        string topic,
        bool askBeforeTell,
        ModelProfile requestedProfile,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repositoryPath);
        ValidateEnum(level, nameof(level));
        ValidateEnum(requestedProfile, nameof(requestedProfile));
        topic = ValidateText(topic, ProjectLearningLimits.MaximumTopicCharacters, "topic_invalid");

        GroundedProjectAnswer answer = await _evidenceSource.GetTutorEvidenceAsync(
            repositoryPath,
            level,
            topic,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectClaim> claims = SelectClaims(answer.Claims);
        EnsureEvidence(claims);
        ModelProfileSelection selection = await _profileRouter.BeginSessionAsync(
            requestedProfile,
            cancellationToken).ConfigureAwait(false);

        try
        {
            TutorGenerationOutput generated = await _model.GenerateTutorTurnAsync(
                new TutorGenerationRequest(
                    level,
                    askBeforeTell ? TutorInteractionKind.AskQuestion : TutorInteractionKind.Explain,
                    topic,
                    null,
                    askBeforeTell,
                    claims,
                    [],
                    ProjectLearningLimits.MaximumResponseCharacters),
                cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            TutorTurn turn = CreateTutorTurn(
                1,
                level,
                askBeforeTell ? TutorInteractionKind.AskQuestion : TutorInteractionKind.Explain,
                generated,
                claims,
                answer.Metrics.ContextBudget,
                now);
            Guid sessionId = Guid.NewGuid();
            ProjectLearningSessionSnapshot session = new(
                sessionId,
                LearningSessionKind.Tutor,
                LearningSessionStatus.Active,
                repositoryPath,
                answer.SnapshotId,
                requestedProfile,
                selection.Selected,
                selection.FellBack,
                selection.ReasonCode,
                level,
                askBeforeTell,
                null,
                0,
                [turn],
                [],
                null,
                CreateTutorTranscript(turn, now),
                turn.ObservedStrengths,
                turn.ObservedGaps,
                null,
                now,
                now);
            await _store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            return CreateResult(session, turn, null, null, null, readyToComplete: false);
        }
        catch
        {
            await TryEndProfileAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<ProjectLearningTurnResult> ContinueTutorCoreAsync(
        Guid sessionId,
        TutorInteractionKind interaction,
        string userInput,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ValidateEnum(interaction, nameof(interaction));
        userInput = ValidateText(
            userInput,
            ProjectLearningLimits.MaximumAnswerCharacters,
            "tutor_input_invalid");
        ProjectLearningSessionSnapshot session = await LoadActiveAsync(
            sessionId,
            LearningSessionKind.Tutor,
            cancellationToken).ConfigureAwait(false);
        ModelProfileSelection profile = await _profileRouter.BeginSessionAsync(
            session.SelectedProfile,
            cancellationToken).ConfigureAwait(false);
        if (session.TutorTurns.Count >= ProjectLearningLimits.MaximumTurns)
        {
            throw new ProjectLearningException("learning_turn_limit");
        }

        TutorLevel level = interaction == TutorInteractionKind.GoDeeper
            ? NextTutorLevel(session.TutorLevel ?? TutorLevel.Foundation)
            : session.TutorLevel ?? TutorLevel.Foundation;
        string topic = interaction == TutorInteractionKind.Recap
            ? string.Join(", ", session.Gaps.Take(4))
            : userInput;
        GroundedProjectAnswer answer = await _evidenceSource.GetTutorEvidenceAsync(
            session.RepositoryPath,
            level,
            topic,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectClaim> claims = SelectClaims(answer.Claims);
        EnsureEvidence(claims);
        TutorGenerationOutput generated = await _model.GenerateTutorTurnAsync(
            new TutorGenerationRequest(
                level,
                interaction,
                topic,
                interaction == TutorInteractionKind.SelfExplanation ? userInput : null,
                session.AskBeforeTell || interaction == TutorInteractionKind.AskQuestion,
                claims,
                session.TutorTurns.TakeLast(_configuration.MaximumRecentTurns).ToArray(),
                ProjectLearningLimits.MaximumResponseCharacters),
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TutorTurn turn = CreateTutorTurn(
            session.TutorTurns.Count + 1,
            level,
            interaction,
            generated,
            claims,
            answer.Metrics.ContextBudget,
            now);
        LearningTranscriptEntry[] transcript = AppendTranscript(
            session.Transcript,
            new LearningTranscriptEntry(
                session.Transcript.Count + 1,
                "user",
                userInput,
                now),
            new LearningTranscriptEntry(
                session.Transcript.Count + 2,
                "assistant",
                CombineTutorOutput(turn),
                now));
        ProjectLearningSessionSnapshot updated = session with
        {
            SelectedProfile = profile.Selected,
            ProfileFellBack = session.ProfileFellBack || profile.FellBack,
            ProfileReasonCode = profile.ReasonCode,
            RepositorySnapshotId = answer.SnapshotId,
            TutorLevel = level,
            AskBeforeTell = session.AskBeforeTell || interaction == TutorInteractionKind.AskQuestion,
            TutorTurns = AppendBounded(session.TutorTurns, turn),
            Transcript = transcript,
            Strengths = MergeLabels(session.Strengths, turn.ObservedStrengths),
            Gaps = MergeLabels(session.Gaps, turn.ObservedGaps),
            UpdatedAt = now,
        };
        await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return CreateResult(updated, turn, null, null, null, readyToComplete: false);
    }

    private async ValueTask<ProjectLearningTurnResult> StartInterviewCoreAsync(
        string repositoryPath,
        InterviewDifficulty difficulty,
        int questionCount,
        ModelProfile requestedProfile,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repositoryPath);
        ValidateEnum(difficulty, nameof(difficulty));
        ValidateEnum(requestedProfile, nameof(requestedProfile));
        if (questionCount < _configuration.MinimumInterviewQuestions ||
            questionCount > _configuration.MaximumInterviewQuestions)
        {
            throw new ProjectLearningException("interview_question_count_invalid");
        }

        InterviewDimension dimension = InterviewOrder[0];
        GroundedProjectAnswer answer = await _evidenceSource.GetInterviewEvidenceAsync(
            repositoryPath,
            dimension,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectClaim> claims = SelectClaims(answer.Claims);
        EnsureEvidence(claims);
        ModelProfileSelection selection = await _profileRouter.BeginSessionAsync(
            requestedProfile,
            cancellationToken).ConfigureAwait(false);

        try
        {
            InterviewQuestion question = await GenerateQuestionAsync(
                difficulty,
                dimension,
                1,
                isFollowUp: false,
                parentQuestionId: null,
                [],
                claims,
                [],
                cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            Guid sessionId = Guid.NewGuid();
            ProjectLearningSessionSnapshot session = new(
                sessionId,
                LearningSessionKind.Interview,
                LearningSessionStatus.Active,
                repositoryPath,
                answer.SnapshotId,
                requestedProfile,
                selection.Selected,
                selection.FellBack,
                selection.ReasonCode,
                null,
                false,
                difficulty,
                questionCount,
                [],
                [],
                question,
                [new LearningTranscriptEntry(1, "assistant", question.Text, now)],
                [],
                [],
                null,
                now,
                now);
            await _store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            return CreateResult(session, null, question, null, null, readyToComplete: false);
        }
        catch
        {
            await TryEndProfileAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<ProjectLearningTurnResult> SubmitInterviewAnswerCoreAsync(
        Guid sessionId,
        string answer,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        answer = ValidateText(
            answer,
            ProjectLearningLimits.MaximumAnswerCharacters,
            "interview_answer_invalid");
        ProjectLearningSessionSnapshot session = await LoadActiveAsync(
            sessionId,
            LearningSessionKind.Interview,
            cancellationToken).ConfigureAwait(false);
        ModelProfileSelection profile = await _profileRouter.BeginSessionAsync(
            session.SelectedProfile,
            cancellationToken).ConfigureAwait(false);
        InterviewQuestion question = session.CurrentQuestion ??
            throw new ProjectLearningException("interview_question_missing");
        GroundedProjectAnswer evidenceAnswer = await _evidenceSource.GetInterviewEvidenceAsync(
            session.RepositoryPath,
            question.Dimension,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectClaim> claims = SelectClaims(evidenceAnswer.Claims);
        EnsureEvidence(claims);
        InterviewAnswerAssessmentOutput assessment = await _model.AssessInterviewAnswerAsync(
            new InterviewAnswerAssessmentRequest(
                session.InterviewDifficulty ?? InterviewDifficulty.Internship,
                question,
                answer,
                claims),
            cancellationToken).ConfigureAwait(false);
        InterviewEvaluation evaluation = CreateEvaluation(question, assessment, claims);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        InterviewTurn completedTurn = new(question, answer, evaluation, now);
        InterviewTurn[] turns = AppendBounded(session.InterviewTurns, completedTurn);
        bool ready = turns.Length >= session.TargetQuestionCount;
        InterviewQuestion? nextQuestion = null;
        string nextAssistantText = evaluation.RequiresTargetedFollowUp && !ready
            ? TargetedFollowUpRationale
            : CreateEvaluationSummary(evaluation);
        if (!ready)
        {
            bool followUp = evaluation.RequiresTargetedFollowUp;
            if (followUp)
            {
                nextQuestion = CreateTargetedFollowUpQuestion(question, turns.Length + 1);
            }
            else
            {
                InterviewDimension nextDimension = InterviewOrder[turns.Length % InterviewOrder.Length];
                GroundedProjectAnswer nextEvidence = nextDimension == question.Dimension
                    ? evidenceAnswer
                    : await _evidenceSource.GetInterviewEvidenceAsync(
                        session.RepositoryPath,
                        nextDimension,
                        cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ProjectClaim> nextClaims = SelectClaims(nextEvidence.Claims);
                EnsureEvidence(nextClaims);
                nextQuestion = await GenerateQuestionAsync(
                    session.InterviewDifficulty ?? InterviewDifficulty.Internship,
                    nextDimension,
                    turns.Length + 1,
                    isFollowUp: false,
                    parentQuestionId: null,
                    [],
                    nextClaims,
                    turns.TakeLast(_configuration.MaximumRecentTurns).ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }

            nextAssistantText = $"{nextAssistantText} {nextQuestion.Text}";
        }

        LearningTranscriptEntry[] transcript = AppendTranscript(
            session.Transcript,
            new LearningTranscriptEntry(session.Transcript.Count + 1, "user", answer, now),
            new LearningTranscriptEntry(
                session.Transcript.Count + 2,
                "assistant",
                nextAssistantText,
                now));
        ProjectLearningSessionSnapshot updated = session with
        {
            SelectedProfile = profile.Selected,
            ProfileFellBack = session.ProfileFellBack || profile.FellBack,
            ProfileReasonCode = profile.ReasonCode,
            RepositorySnapshotId = evidenceAnswer.SnapshotId,
            InterviewTurns = turns,
            CurrentQuestion = nextQuestion,
            Transcript = transcript,
            Strengths = MergeLabels(session.Strengths, evaluation.Strengths),
            Gaps = MergeLabels(session.Gaps, evaluation.Gaps),
            UpdatedAt = now,
        };
        await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        InterviewEvaluation presentedEvaluation = evaluation.RequiresTargetedFollowUp && !ready
            ? evaluation with
            {
                Gaps = [],
                Rationale = TargetedFollowUpRationale,
                Corrections = [],
                Scores = evaluation.Scores.Select(static score => score with
                {
                    Rationale = TargetedFollowUpRationale,
                }).ToArray(),
            }
            : evaluation;
        return CreateResult(updated, null, nextQuestion, presentedEvaluation, null, ready);
    }

    private async ValueTask<ProjectLearningTurnResult> EndSessionCoreAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        ProjectLearningSessionSnapshot session = await LoadSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session.Status != LearningSessionStatus.Active)
        {
            await _profileRouter.EndSessionAsync(cancellationToken).ConfigureAwait(false);
            return CreateResult(
                session,
                LastOrDefault(session.TutorTurns),
                session.CurrentQuestion,
                LastOrDefault(session.InterviewTurns)?.Evaluation,
                session.Report,
                readyToComplete: true);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        ProjectLearningReport report = CreateReport(session, now);
        ProjectLearningSessionSnapshot completed = session with
        {
            Status = LearningSessionStatus.Completed,
            CurrentQuestion = null,
            Report = report,
            UpdatedAt = now,
        };
        await _store.SaveAsync(completed, cancellationToken).ConfigureAwait(false);
        await _profileRouter.EndSessionAsync(cancellationToken).ConfigureAwait(false);
        return CreateResult(
            completed,
            LastOrDefault(completed.TutorTurns),
            null,
            LastOrDefault(completed.InterviewTurns)?.Evaluation,
            report,
            readyToComplete: true);
    }

    private async ValueTask<ProjectLearningTurnResult> StartRevisionCoreAsync(
        string repositoryPath,
        ModelProfile requestedProfile,
        CancellationToken cancellationToken)
    {
        ValidateRepository(repositoryPath);
        ProjectLearningSessionSnapshot previous = await _store.LoadLatestCompletedInterviewAsync(
            repositoryPath,
            cancellationToken).ConfigureAwait(false) ??
            throw new ProjectLearningException("completed_interview_not_found");
        string topic = previous.Report is { RevisionTopics.Count: > 0 }
            ? previous.Report.RevisionTopics[0]
            : previous.Gaps.Count > 0
                ? previous.Gaps[0]
                :
            throw new ProjectLearningException("revision_topics_not_found");
        TutorLevel level = InferTutorLevel(topic);
        return await StartTutorCoreAsync(
            repositoryPath,
            level,
            topic,
            askBeforeTell: true,
            requestedProfile,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ProjectLearningTurnResult> RunExclusiveAsync(
        Func<CancellationToken, ValueTask<ProjectLearningTurnResult>> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<InterviewQuestion> GenerateQuestionAsync(
        InterviewDifficulty difficulty,
        InterviewDimension dimension,
        int sequence,
        bool isFollowUp,
        Guid? parentQuestionId,
        IReadOnlyList<string> targetGaps,
        IReadOnlyList<ProjectClaim> claims,
        IReadOnlyList<InterviewTurn> recentTurns,
        CancellationToken cancellationToken)
    {
        InterviewQuestionGenerationOutput output = await _model.GenerateInterviewQuestionAsync(
            new InterviewQuestionGenerationRequest(
                difficulty,
                dimension,
                sequence,
                isFollowUp,
                targetGaps,
                claims,
                recentTurns,
                ProjectLearningLimits.MaximumResponseCharacters),
            cancellationToken).ConfigureAwait(false);
        string question = NormalizeGeneratedText(output.Question, "interview_question_invalid");
        ProjectEvidence[] evidence = SelectEvidence(output.EvidenceIndexes, claims);
        if (evidence.Length == 0)
        {
            throw new ProjectLearningException("interview_question_evidence_required");
        }

        IReadOnlyList<string> concepts = CreateGroundedConcepts(output.EvidenceIndexes, claims);
        return new InterviewQuestion(
            Guid.NewGuid(),
            sequence,
            dimension,
            question,
            concepts,
            evidence,
            isFollowUp,
            parentQuestionId);
    }

    private static InterviewEvaluation CreateEvaluation(
        InterviewQuestion question,
        InterviewAnswerAssessmentOutput assessment,
        IReadOnlyList<ProjectClaim> claims)
    {
        HashSet<int> demonstrated = ValidIndexes(
            assessment.DemonstratedConceptIndexes,
            question.ExpectedConcepts.Count);
        HashSet<int> incorrect = ValidIndexes(
            assessment.IncorrectConceptIndexes,
            question.ExpectedConcepts.Count);
        double ratio = question.ExpectedConcepts.Count == 0
            ? 0
            : (double)demonstrated.Count / question.ExpectedConcepts.Count;
        int factual = ClampScore((int)Math.Round(ratio * 4, MidpointRounding.AwayFromZero) - incorrect.Count);
        int depth = ClampScore((int)Math.Round(ratio * 4, MidpointRounding.AwayFromZero));
        string rationale = CreateAssessmentRationale(
            demonstrated.Count,
            question.ExpectedConcepts.Count,
            incorrect.Count);
        List<DimensionScore> scores =
        [
            Score(ScoreDimension.ProjectFactualAccuracy, factual, "Correct project facts with calibrated uncertainty and no unsupported claims.", rationale),
            Score(ScoreDimension.TechnicalDepth, depth, "Explains the expected concepts beyond surface terminology.", rationale),
            Score(ScoreDimension.Reasoning, FlagScore(assessment.ShowsReasoning, ratio), "Connects claims through a coherent technical reason.", rationale),
            Score(ScoreDimension.TradeOffAwareness, FlagScore(assessment.ShowsTradeOffAwareness, ratio), "Identifies costs, alternatives, and consequences.", rationale),
            Score(ScoreDimension.CSharpDotNetUnderstanding, FlagScore(assessment.ShowsCSharpDotNetUnderstanding, ratio), "Applies relevant C# and .NET semantics accurately.", rationale),
            Score(ScoreDimension.DatabaseUnderstanding, FlagScore(assessment.ShowsDatabaseUnderstanding, ratio), "Explains observed data access and database behavior accurately.", rationale),
            Score(ScoreDimension.TestingUnderstanding, FlagScore(assessment.ShowsTestingUnderstanding, ratio), "Explains useful tests, boundaries, and failure verification.", rationale),
            Score(ScoreDimension.SecurityAwareness, FlagScore(assessment.ShowsSecurityAwareness, ratio), "Recognizes trust boundaries, validation, authorization, and privacy.", rationale),
            Score(ScoreDimension.CommunicationClarity, ClampScore(assessment.CommunicationClarity), "Communicates a direct, organized, technically precise answer.", rationale),
            Score(ScoreDimension.ConfidenceCalibration, ClampScore(assessment.ConfidenceCalibration), "Separates known project facts from assumptions and alternatives.", rationale),
        ];
        double overall = Math.Round(scores.Average(static score => score.Score), 2);
        string[] strengths = demonstrated
            .Select(index => question.ExpectedConcepts[index])
            .Take(6)
            .ToArray();
        string[] gaps = Enumerable.Range(0, question.ExpectedConcepts.Count)
            .Where(index => !demonstrated.Contains(index) || incorrect.Contains(index))
            .Select(index => question.ExpectedConcepts[index])
            .Take(6)
            .ToArray();
        IReadOnlyList<LearningStatement> corrections = CreateCorrections(assessment, claims);
        return new InterviewEvaluation(
            question.QuestionId,
            scores,
            overall,
            strengths,
            gaps,
            rationale,
            corrections,
            overall < 2.5 || incorrect.Count > 0 || gaps.Length > question.ExpectedConcepts.Count / 2);
    }

    private static LearningStatement[] CreateCorrections(
        InterviewAnswerAssessmentOutput assessment,
        IReadOnlyList<ProjectClaim> claims)
    {
        if (string.IsNullOrWhiteSpace(assessment.Correction))
        {
            return [];
        }

        LearningStatement[] corrections = ValidIndexes(
                assessment.CorrectionEvidenceIndexes,
                claims.Count)
            .OrderBy(static index => index)
            .Select(index => claims[index])
            .Where(static claim =>
                claim.Classification == ProjectKnowledgeClassification.ProjectFact &&
                claim.Evidence.Count > 0)
            .Take(3)
            .Select(static claim => new LearningStatement(
                LearningStatementKind.ProjectFact,
                claim.Statement,
                claim.Evidence.Take(ProjectLearningLimits.MaximumTurnEvidenceItems).ToArray()))
            .ToArray();
        if (corrections.Length == 0)
        {
            throw new ProjectLearningException("assessment_correction_evidence_required");
        }

        return corrections;
    }

    private TutorTurn CreateTutorTurn(
        int sequence,
        TutorLevel level,
        TutorInteractionKind interaction,
        TutorGenerationOutput output,
        IReadOnlyList<ProjectClaim> claims,
        ProjectContextBudget sourceBudget,
        DateTimeOffset now)
    {
        string explanation = string.IsNullOrWhiteSpace(output.Explanation)
            ? "Consider the project evidence before answering the next question."
            : NormalizeGeneratedText(output.Explanation, "tutor_explanation_invalid");
        string? question = string.IsNullOrWhiteSpace(output.SocraticQuestion)
            ? null
            : NormalizeGeneratedText(output.SocraticQuestion, "tutor_question_invalid");
        if ((interaction == TutorInteractionKind.AskQuestion || interaction == TutorInteractionKind.SelfExplanation) &&
            question is null)
        {
            throw new ProjectLearningException("tutor_question_required");
        }

        ProjectEvidence[] evidence = SelectEvidence(output.EvidenceIndexes, claims);
        if (evidence.Length == 0)
        {
            throw new ProjectLearningException("tutor_evidence_required");
        }

        List<LearningStatement> statements = [];
        foreach (int index in ValidIndexes(output.EvidenceIndexes, claims.Count).OrderBy(static index => index))
        {
            ProjectClaim claim = claims[index];
            if (claim.Classification != ProjectKnowledgeClassification.ProjectFact ||
                claim.Evidence.Count == 0)
            {
                continue;
            }

            statements.Add(new LearningStatement(
                LearningStatementKind.ProjectFact,
                claim.Statement,
                claim.Evidence.Take(1).ToArray()));
            if (statements.Count >= 4)
            {
                break;
            }
        }

        statements.Add(new LearningStatement(
            LearningStatementKind.GeneralPrinciple,
            explanation,
            []));
        foreach (string alternative in NormalizeLabels(output.DesignAlternatives).Take(3))
        {
            statements.Add(new LearningStatement(
                LearningStatementKind.DesignAlternative,
                alternative,
                []));
        }
        int usedCharacters = statements.Sum(static statement => statement.Text.Length) +
            evidence.Sum(static item => item.Excerpt.Length) + (question?.Length ?? 0);
        ProjectContextBudget budget = new(
            Math.Min(_configuration.MaximumContextCharacters, sourceBudget.MaximumCharacters),
            Math.Min(usedCharacters, _configuration.MaximumContextCharacters),
            (Math.Min(usedCharacters, _configuration.MaximumContextCharacters) + 3) / 4,
            evidence.Length,
            sourceBudget.Truncated || usedCharacters > _configuration.MaximumContextCharacters);
        return new TutorTurn(
            sequence,
            level,
            interaction,
            explanation,
            question,
            CreateGroundedConcepts(output.EvidenceIndexes, claims),
            statements,
            NormalizeLabels(output.ObservedStrengths),
            NormalizeLabels(output.ObservedGaps),
            budget,
            now);
    }

    private static ProjectLearningReport CreateReport(
        ProjectLearningSessionSnapshot session,
        DateTimeOffset completedAt)
    {
        double overall = session.InterviewTurns.Count == 0
            ? 0
            : session.InterviewTurns.Average(static turn => turn.Evaluation.OverallScore);
        double Category(params InterviewDimension[] dimensions)
        {
            double[] values = session.InterviewTurns
                .Where(turn => dimensions.Contains(turn.Question.Dimension))
                .Select(static turn => turn.Evaluation.OverallScore)
                .ToArray();
            return Math.Round(values.Length == 0 ? overall : values.Average(), 2);
        }

        LearningReportCategory[] categories =
        [
            new("Project Knowledge", Category(InterviewDimension.ProjectOverview)),
            new("Architecture", Category(InterviewDimension.Architecture, InterviewDimension.DesignTradeOffs)),
            new("Implementation", Category(InterviewDimension.ActualImplementation, InterviewDimension.ApiDesign)),
            new("C#/.NET", Category(InterviewDimension.CSharpDotNet, InterviewDimension.Concurrency)),
            new("Database", Category(InterviewDimension.Database)),
            new("Testing", Category(InterviewDimension.Testing)),
            new("Security", Category(InterviewDimension.Security)),
            new("Failure Handling", Category(InterviewDimension.ErrorHandling, InterviewDimension.FailureScenarios)),
            new("Tradeoffs", Category(InterviewDimension.DesignTradeOffs, InterviewDimension.Scalability, InterviewDimension.Performance)),
            new("Communication", AverageDimension(session.InterviewTurns, ScoreDimension.CommunicationClarity, overall)),
        ];
        string[] strong = MergeLabels(
            session.Strengths,
            categories.Where(static category => category.Score >= 3.0).Select(static category => category.Name));
        string[] weak = MergeLabels(
            session.Gaps,
            categories.Where(static category => category.Score < 2.5).Select(static category => category.Name));
        string[] poor = session.InterviewTurns
            .Where(static turn => turn.Evaluation.OverallScore < 2.5)
            .Select(static turn => turn.Question.Text)
            .Take(10)
            .ToArray();
        InterviewDifficulty current = session.InterviewDifficulty ?? InterviewDifficulty.Internship;
        InterviewDifficulty suggested = overall >= 3.4
            ? current switch
            {
                InterviewDifficulty.Internship => InterviewDifficulty.Junior,
                InterviewDifficulty.Junior => InterviewDifficulty.MidLevelStretch,
                _ => InterviewDifficulty.MidLevelStretch,
            }
            : current;
        return new ProjectLearningReport(
            session.SessionId,
            categories,
            strong,
            weak,
            poor,
            weak.Take(8).ToArray(),
            suggested,
            completedAt);
    }

    private List<ProjectClaim> SelectClaims(IReadOnlyList<ProjectClaim> claims)
    {
        List<ProjectClaim> selected = [];
        int characters = 0;
        foreach (ProjectClaim claim in claims
            .OrderBy(static claim => claim.Classification == ProjectKnowledgeClassification.ProjectFact ? 0 : 1))
        {
            if (selected.Count >= _configuration.MaximumEvidenceItems)
            {
                break;
            }

            int cost = claim.Statement.Length + claim.Evidence.Sum(static evidence =>
                evidence.RelativePath.Length + evidence.Excerpt.Length + 64);
            if (characters + cost > _configuration.MaximumContextCharacters)
            {
                continue;
            }

            selected.Add(claim);
            characters += cost;
        }

        return selected;
    }

    private static void EnsureEvidence(IReadOnlyList<ProjectClaim> claims)
    {
        if (!claims.Any(static claim =>
            claim.Classification == ProjectKnowledgeClassification.ProjectFact && claim.Evidence.Count > 0))
        {
            throw new ProjectLearningException("project_evidence_required");
        }
    }

    private async ValueTask<ProjectLearningSessionSnapshot> LoadActiveAsync(
        Guid sessionId,
        LearningSessionKind expectedKind,
        CancellationToken cancellationToken)
    {
        ProjectLearningSessionSnapshot session = await LoadSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session.Kind != expectedKind)
        {
            throw new ProjectLearningException("learning_session_kind_mismatch");
        }

        if (session.Status != LearningSessionStatus.Active)
        {
            throw new ProjectLearningException("learning_session_not_active");
        }

        return session;
    }

    private async ValueTask<ProjectLearningSessionSnapshot> LoadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await _store.LoadAsync(sessionId, cancellationToken).ConfigureAwait(false) ??
            throw new ProjectLearningException("learning_session_not_found");

    private static ProjectLearningTurnResult CreateResult(
        ProjectLearningSessionSnapshot session,
        TutorTurn? tutorTurn,
        InterviewQuestion? question,
        InterviewEvaluation? evaluation,
        ProjectLearningReport? report,
        bool readyToComplete) =>
        new(
            session.SessionId,
            session.Kind,
            session.Status,
            new ModelProfileSelection(
                session.RequestedProfile,
                session.SelectedProfile,
                session.ProfileFellBack,
                session.ProfileReasonCode),
            tutorTurn,
            question,
            evaluation,
            report,
            readyToComplete);

    private static ProjectEvidence[] SelectEvidence(
        IReadOnlyList<int> requestedIndexes,
        IReadOnlyList<ProjectClaim> claims)
    {
        HashSet<int> indexes = ValidIndexes(requestedIndexes, claims.Count);
        return indexes
            .OrderBy(static index => index)
            .SelectMany(index => claims[index].Evidence)
            .DistinctBy(static evidence =>
                $"{evidence.RelativePath}|{evidence.StartLine}|{evidence.EndLine}|{evidence.ContentHash}",
                StringComparer.Ordinal)
            .Take(ProjectLearningLimits.MaximumTurnEvidenceItems)
            .ToArray();
    }

    private static HashSet<int> ValidIndexes(IReadOnlyList<int>? indexes, int maximumExclusive) =>
        indexes is null
            ? []
            : indexes.Where(index => index >= 0 && index < maximumExclusive).ToHashSet();

    private static string[] CreateGroundedConcepts(
        IReadOnlyList<int> evidenceIndexes,
        IReadOnlyList<ProjectClaim> claims) =>
        ValidIndexes(evidenceIndexes, claims.Count)
            .OrderBy(static index => index)
            .Select(index => claims[index])
            .Where(static claim =>
                claim.Classification == ProjectKnowledgeClassification.ProjectFact &&
                claim.Evidence.Count > 0)
            .Select(static claim => NormalizeLabel(claim.Statement))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(ProjectLearningLimits.MaximumConcepts)
            .ToArray();

    private static InterviewQuestion CreateTargetedFollowUpQuestion(
        InterviewQuestion previousQuestion,
        int sequence) => new(
            Guid.NewGuid(),
            sequence,
            previousQuestion.Dimension,
            "Re-examine your previous answer using the repository evidence. Which specific fact supports or contradicts your assumption, and why?",
            previousQuestion.ExpectedConcepts,
            previousQuestion.Evidence,
            IsFollowUp: true,
            previousQuestion.QuestionId);

    private static string CreateAssessmentRationale(
        int demonstratedConcepts,
        int expectedConcepts,
        int incorrectConcepts)
    {
        if (incorrectConcepts > 0)
        {
            return $"The response contradicted {incorrectConcepts} of {expectedConcepts} grounded expected concepts.";
        }

        return demonstratedConcepts == expectedConcepts
            ? $"The response demonstrated all {expectedConcepts} grounded expected concepts."
            : $"The response demonstrated {demonstratedConcepts} of {expectedConcepts} grounded expected concepts.";
    }

    private static string[] NormalizeLabels(IEnumerable<string>? labels) =>
        labels is null
            ? []
            : labels
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => NormalizeLabel(value))
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(ProjectLearningLimits.MaximumConcepts)
                .ToArray();

    private static string NormalizeLabel(string value)
    {
        string normalized = string.Join(' ', value.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 256 ? normalized : normalized[..256];
    }

    private static string[] MergeLabels(
        IEnumerable<string> existing,
        IEnumerable<string> added) =>
        existing.Concat(added)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => NormalizeLabel(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();

    private static T[] AppendBounded<T>(IReadOnlyList<T> existing, T item) =>
        existing.Append(item).TakeLast(ProjectLearningLimits.MaximumTurns).ToArray();

    private static LearningTranscriptEntry[] AppendTranscript(
        IReadOnlyList<LearningTranscriptEntry> existing,
        params LearningTranscriptEntry[] entries) =>
        existing.Concat(entries).TakeLast(ProjectLearningLimits.MaximumTurns * 2).ToArray();

    private static LearningTranscriptEntry[] CreateTutorTranscript(
        TutorTurn turn,
        DateTimeOffset now) =>
        [new LearningTranscriptEntry(1, "assistant", CombineTutorOutput(turn), now)];

    private static string CombineTutorOutput(TutorTurn turn) =>
        turn.SocraticQuestion is null
            ? turn.Explanation
            : $"{turn.Explanation} {turn.SocraticQuestion}";

    private static string CreateEvaluationSummary(InterviewEvaluation evaluation)
    {
        string correction = evaluation.Corrections.Count > 0
            ? evaluation.Corrections[0].Text
            : string.Empty;
        return string.IsNullOrEmpty(correction)
            ? $"Assessment: {evaluation.OverallScore:F1} out of 4. {evaluation.Rationale}"
            : $"Assessment: {evaluation.OverallScore:F1} out of 4. {evaluation.Rationale} Correction: {correction}";
    }

    private static DimensionScore Score(
        ScoreDimension dimension,
        int score,
        string rubric,
        string rationale) =>
        new(dimension, ClampScore(score), rubric, rationale);

    private static int FlagScore(bool flag, double conceptRatio) =>
        ClampScore((flag ? 2 : 0) + (int)Math.Round(conceptRatio * 2, MidpointRounding.AwayFromZero));

    private static int ClampScore(int score) => Math.Clamp(score, 0, 4);

    private static T? LastOrDefault<T>(IReadOnlyList<T> values) =>
        values.Count == 0 ? default : values[^1];

    private static double AverageDimension(
        IReadOnlyList<InterviewTurn> turns,
        ScoreDimension dimension,
        double fallback)
    {
        int[] scores = turns.SelectMany(static turn => turn.Evaluation.Scores)
            .Where(score => score.Dimension == dimension)
            .Select(static score => score.Score)
            .ToArray();
        return Math.Round(scores.Length == 0 ? fallback : scores.Average(), 2);
    }

    private static TutorLevel NextTutorLevel(TutorLevel level) =>
        level == TutorLevel.InterviewDefence ? level : (TutorLevel)((int)level + 1);

    private static TutorLevel InferTutorLevel(string topic)
    {
        if (topic.Contains("security", StringComparison.OrdinalIgnoreCase))
        {
            return TutorLevel.Security;
        }

        if (topic.Contains("database", StringComparison.OrdinalIgnoreCase))
        {
            return TutorLevel.Database;
        }

        if (topic.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return TutorLevel.Testing;
        }

        if (topic.Contains("architecture", StringComparison.OrdinalIgnoreCase))
        {
            return TutorLevel.Architecture;
        }

        return TutorLevel.Implementation;
    }

    private static string NormalizeGeneratedText(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > ProjectLearningLimits.MaximumResponseCharacters ||
            value.Contains('\0'))
        {
            throw new ProjectLearningException(code);
        }

        return value.Trim();
    }

    private static string ValidateText(string value, int maximumCharacters, string code)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumCharacters ||
            value.Contains('\0'))
        {
            throw new ProjectLearningException(code);
        }

        return value.Trim();
    }

    private static void ValidateRepository(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) ||
            repositoryPath.Length > 2_048 ||
            repositoryPath.Any(char.IsControl))
        {
            throw new ProjectLearningException("repository_path_invalid");
        }
    }

    private static void ValidateSessionId(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ProjectLearningException("learning_session_id_invalid");
        }
    }

    private static void ValidateEnum<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateConfiguration(ProjectLearningConfiguration configuration)
    {
        if (configuration.MaximumContextCharacters is < 4_096 or > 32_768 ||
            configuration.MaximumEvidenceItems is < 1 or > ProjectLearningLimits.MaximumEvidenceItems ||
            configuration.MaximumRecentTurns is < 1 or > 12 ||
            configuration.MinimumInterviewQuestions is < 5 or > 20 ||
            configuration.MaximumInterviewQuestions < configuration.MinimumInterviewQuestions ||
            configuration.MaximumInterviewQuestions > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    private async ValueTask TryEndProfileAsync()
    {
        try
        {
            await _profileRouter.EndSessionAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException and
                not AccessViolationException)
        {
        }
    }
}
