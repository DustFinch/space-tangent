// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

/// <summary>The existing named-model preference format, independent of engine path discovery.</summary>
public static class DeltaTPreferenceFile
{
    public static DeltaTModel Load(string path)
    {
        try
        {
            if (Enum.TryParse(File.ReadAllText(path), out DeltaTModel selected) && Enum.IsDefined(selected)) return selected;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A fresh profile has no preference yet.
        }
        return DeltaTModel.EspenakMeeus2006;
    }

    public static void Save(string path, DeltaTModel model)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, model.ToString());
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
