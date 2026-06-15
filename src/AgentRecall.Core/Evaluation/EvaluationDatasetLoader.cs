using System.Text.Json;

namespace AgentRecall.Core.Evaluation;

/// <summary>Loads an <see cref="EvaluationDataset"/> from the embedded default or a file.</summary>
public static class EvaluationDatasetLoader
{
    /// <summary>Manifest name of the bundled retrieval dataset.</summary>
    public const string DefaultResourceName = "AgentRecall.Core.Evaluation.retrieval-eval.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads the dataset bundled with the build.</summary>
    public static EvaluationDataset LoadDefault()
    {
        var assembly = typeof(EvaluationDatasetLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"Embedded evaluation dataset '{DefaultResourceName}' was not found.");

        return Deserialize(stream);
    }

    /// <summary>Loads a dataset from a JSON file.</summary>
    public static EvaluationDataset LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Evaluation dataset not found: {path}", path);
        }

        using var stream = File.OpenRead(path);
        return Deserialize(stream);
    }

    private static EvaluationDataset Deserialize(Stream stream) =>
        JsonSerializer.Deserialize<EvaluationDataset>(stream, Options)
            ?? throw new InvalidOperationException("Evaluation dataset deserialized to null.");
}
