using Microsoft.ML.OnnxRuntime;

namespace DecisionInference.Julia;

/// <summary>Native Julia-1 composition using the same public prepare, score and decode stages.</summary>
public sealed class JuliaDecisionGenerator : IDecisionGenerator
{
    public const string ModelId = "SupersonicLabs/Julia-1-ONNX";
    private readonly PrepareDecisionInputs _prepare;
    private readonly ScoreOnnxDecisionModel _score;
    private readonly DecodeDecisions _decode;
    private readonly bool _ownsStages;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;
    private static readonly DecisionGeneratorMetadata Metadata = new("julia", defaultModelId: ModelId,
        supportedKinds: [DecisionKind.Binary, DecisionKind.Choice, DecisionKind.Score]);

    public JuliaDecisionGenerator(PrepareDecisionInputs prepare, ScoreOnnxDecisionModel score, DecodeDecisions decode, bool ownsStages = false)
    {
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        _score = score ?? throw new ArgumentNullException(nameof(score));
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        _ownsStages = ownsStages;
    }

    public JuliaDecisionGenerator(string modelPath, string tokenizerJsonPath, SessionOptions? sessionOptions = null)
    {
        _prepare = new(tokenizerJsonPath);
        try { _score = ScoreOnnxDecisionModel.Load(modelPath, sessionOptions); }
        catch { _prepare.Dispose(); throw; }
        _decode = new();
        _ownsStages = true;
    }

    /// <summary>Loads model.onnx and tokenizer.json from one explicit local directory. No files are downloaded.</summary>
    public static JuliaDecisionGenerator LoadFromDirectory(string modelDirectory, Action<SessionOptions>? configureSession = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        if (!Directory.Exists(modelDirectory)) throw new DirectoryNotFoundException($"Julia model directory not found: {modelDirectory}");
        return Load(new()
        {
            ModelPath = Path.Combine(modelDirectory, "model.onnx"),
            TokenizerJsonPath = Path.Combine(modelDirectory, "tokenizer.json"),
            ConfigureSession = configureSession
        });
    }

    /// <summary>
    /// Loads an owning generator synchronously. Temporary session options are disposed on success or failure;
    /// configuration and native loading exceptions propagate unchanged.
    /// </summary>
    public static JuliaDecisionGenerator Load(JuliaDecisionGeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string modelPath = RequireFile(options.ModelPath, nameof(options.ModelPath));
        string tokenizerPath = RequireFile(options.TokenizerJsonPath, nameof(options.TokenizerJsonPath));
        using var sessionOptions = new SessionOptions();
        options.ConfigureSession?.Invoke(sessionOptions);
        return new(modelPath, tokenizerPath, sessionOptions);
    }

    private static string RequireFile(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("An explicit local Julia artifact is required.", fullPath);
        return fullPath;
    }

    public Task<DecisionResult> GenerateAsync(DecisionInput input, DecisionGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (options?.ModelId is { } model && !string.Equals(model, ModelId, StringComparison.Ordinal))
            throw new NotSupportedException("This local Julia generator cannot switch its loaded model.");
        _gate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var batch = _prepare.Prepare(input, cancellationToken);
            var output = _score.Score(batch, cancellationToken);
            var result = _decode.Decode(batch, output);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        finally { _gate.Release(); }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        return serviceType == typeof(DecisionGeneratorMetadata) ? Metadata : serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_ownsStages) { _score.Dispose(); _prepare.Dispose(); }
            }
        }
        finally { _gate.Release(); }
    }
}
