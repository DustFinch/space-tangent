// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private FileDialog AddTourDialogs()
    {
        var source = new FileDialog { Name = "ConvertTourSource", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile, DeletingEnabled = false };
        source.AddFilter("*.cel,*.celx", "Tour sources");
        AddChild(source);
        var destination = new FileDialog { Name = "ConvertedTourTarget", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.SaveFile, DeletingEnabled = false };
        destination.AddFilter("*.lua", "SpaceTangent Lua");
        AddChild(destination);
        TourConversion? pending = null;
        source.FileSelected += path =>
        {
            try
            {
                pending = TourConverter.ConvertFile(path);
                destination.CurrentFile = Path.GetFileNameWithoutExtension(path) + ".lua";
                destination.PopupCentered(new(700, 460));
            }
            catch (Exception error) when (IsLoadError(error))
            {
                _status.Text = $"Could not convert tour. {error.Message}";
            }
        };
        destination.FileSelected += path =>
        {
            if (pending is not { } conversion)
                return;
            try
            {
                File.WriteAllText(path, conversion.Lua);
                _status.Text = conversion.Unapplied.Count == 0 ? "Tour saved as Lua."
                    : $"Tour saved as Lua with {conversion.Unapplied.Count} conversion notes in the file.";
            }
            catch (Exception error) when (IsLoadError(error))
            {
                _status.Text = $"Could not save tour. {error.Message}";
            }
            finally
            {
                pending = null;
            }
        };
        destination.Canceled += () => pending = null;
        return source;
    }
}
