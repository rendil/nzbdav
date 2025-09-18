namespace NzbWebDAV.Api.Controllers.IntegrityResults;

public class IntegrityResultsResponse
{
    public List<IntegrityJobRun> JobRuns { get; set; } = new();
    public List<IntegrityFileResult> AllFiles { get; set; } = new();
}

public class IntegrityJobRun
{
    public DateTime Date { get; set; }
    public int TotalFiles { get; set; }
    public int CorruptFiles { get; set; }
    public int ValidFiles { get; set; }
    public List<IntegrityFileResult> Files { get; set; } = new();
}

public class IntegrityFileResult
{
    public string FileId { get; set; } = null!;
    public string FilePath { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public bool IsLibraryFile { get; set; }
    public DateTime LastChecked { get; set; }
    public string Status { get; set; } = null!; // "valid", "corrupt", "unknown"
}
