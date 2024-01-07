using System.Collections.Generic;
using System.IO;
using System.Linq;

using static Newtonsoft.Json.JsonConvert;

namespace Core.Session;

// this is gonna save the bot session data locally atm
// there will be an AWS session handler later to upload the session data to AWS S3
// the idea is we will have two session data handlers working at the same time
public sealed class LocalGrindSessionDAO : IGrindSessionDAO
{
    private readonly DataConfig dataConfig;

    public LocalGrindSessionDAO(DataConfig dataConfig)
    {
        this.dataConfig = dataConfig;

        if (!Directory.Exists(dataConfig.ExpHistory))
            Directory.CreateDirectory(dataConfig.ExpHistory);
    }

    public IEnumerable<GrindSession> Load()
    {
        List<GrindSession> sessions = Directory.EnumerateFiles(dataConfig.ExpHistory, "*.json")
            .Select(file => DeserializeObject<GrindSession>(File.ReadAllText(file))!)
            .OrderByDescending(grindingSession => grindingSession.SessionStart)
            .ToList();

        if (sessions.Count != 0)
        {
            int[] expList = ExperienceProvider.Get(dataConfig);
            foreach (GrindSession? s in sessions)
            {
                s.ExpList = expList;
            }
        }

        return sessions;
    }

    public void Save(GrindSession session)
    {
        string json = SerializeObject(session);
        File.WriteAllText(Path.Join(dataConfig.ExpHistory, $"{session.SessionId}.json"), json);
    }
}