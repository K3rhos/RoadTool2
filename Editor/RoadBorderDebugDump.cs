using System;
using System.Collections.Generic;
using System.IO;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Writes each network's border debug record to Libraries/RoadTool2/debug/borders.txt whenever its border pieces
/// have been rebuilt with the overlay on, so the numbers can be read outside the editor.
/// </summary>
public static class RoadBorderDebugDump
{
	private static readonly Dictionary<Guid, int> s_Written = new();



	[EditorEvent.Frame]
	public static void OnFrame()
	{
		var scene = SceneEditorSession.Active?.Scene;

		if (scene is null)
			return;

		foreach (var network in scene.GetAllComponents<RoadNetwork>())
		{
			if (!network.IsValid() || !network.DebugBorders || network.IsRebuilding)
				continue;

			if (s_Written.TryGetValue(network.Id, out int revision) && revision == network.DebugRevision)
				continue;

			s_Written[network.Id] = network.DebugRevision;

			try
			{
				string path = GetPath(network);

				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, network.BuildDebugRecord());

				Log.Info($"Road Tool 2: border debug written to {path}");
			}
			catch (Exception exception)
			{
				Log.Warning($"Road Tool 2: could not write the border debug record: {exception.Message}");
			}
		}
	}



	private static string GetPath(RoadNetwork _Network)
	{
		string root = Project.Current.GetRootPath();
		string library = Path.Combine(root, "Libraries", "RoadTool2");

		// The library folder may have been renamed; find it by its project file.
		if (!Directory.Exists(library))
		{
			foreach (string directory in Directory.GetDirectories(Path.Combine(root, "Libraries")))
			{
				if (File.Exists(Path.Combine(directory, "roadtool2.sbproj")))
				{
					library = directory;
					break;
				}
			}
		}

		string name = string.Concat(_Network.GameObject.Name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');

		return Path.Combine(library, "debug", $"borders_{name}.txt");
	}
}
