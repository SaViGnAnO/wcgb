namespace ReadDBC_CSV;

public interface IExtractor
{
    public string[] FileRequirement { get; }

    void Run();
}
