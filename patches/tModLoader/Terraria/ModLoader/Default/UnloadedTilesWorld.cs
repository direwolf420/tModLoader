using System.Collections.Generic;
using System.Linq;
using Terraria.ModLoader.IO;

namespace Terraria.ModLoader.Default
{
	internal class UnloadedTilesWorld : ModWorld
	{
		/// <summary>
		/// <see cref="UnloadedTileInfo"/>s that are not able to be restored in the current state of the world (and saved for the next world load)
		/// </summary>
		internal List<UnloadedTileInfo> infos = new List<UnloadedTileInfo>();

		/// <summary>
		/// Populated during <see cref="TileIO.ReadModTile"/>, detecting tiles that lost their loaded mod, to then turn them into unloaded tiles
		/// </summary>
		internal List<UnloadedTileInfo> pendingInfos = new List<UnloadedTileInfo>();

		//TODO UnloadedWall

		internal static ushort UnloadedType => ModContent.Find<ModTile>("ModLoader/UnloadedTile").Type;

		internal static ushort PendingType => ModContent.Find<ModTile>("ModLoader/PendingUnloadedTile").Type;

		public override void Initialize() {
			infos.Clear();
			pendingInfos.Clear();
		}

		public override TagCompound Save() {
			return new TagCompound {
				["list"] = infos.Select(info => info?.Save() ?? new TagCompound()).ToList()
			};
		}

		public override void Load(TagCompound tag) {
			List<ushort> canRestore = new List<ushort>();
			bool canRestoreFlag = false;
			var list = tag.GetList<TagCompound>("list");
			foreach (var infoTag in list) {
				if (!infoTag.ContainsKey("mod")) {
					//infos entries get nulled out once restored, leading to an empty tag. This reverts it
					infos.Add(null);
					canRestore.Add(0);
					continue;
				}

				string modName = infoTag.GetString("mod");
				string name = infoTag.GetString("name");
				bool frameImportant = infoTag.ContainsKey("frameX");
				var info = frameImportant ?
					new UnloadedTileInfo(modName, name, infoTag.GetShort("frameX"), infoTag.GetShort("frameY")) :
					new UnloadedTileInfo(modName, name);
				infos.Add(info);

				int type = ModContent.TryFind(modName, name, out ModTile tile) ? tile.Type : 0;
				canRestore.Add((ushort)type);
				if (type != 0)
					canRestoreFlag = true;
			}
			if (canRestoreFlag) {
				RestoreTiles(canRestore);
				for (int k = 0; k < canRestore.Count; k++) {
					if (canRestore[k] > 0)
						infos[k] = null; //Restored infos don't need to be saved
				}
			}
			if (pendingInfos.Count > 0)
				ConfirmPendingInfo();
		}

		/// <summary>
		/// Converts unloaded tiles to their original type
		/// </summary>
		/// <param name="canRestore">List of types that can be restored, indexed by the tiles frameID through <see cref="UnloadedTileFrame"/></param>
		private void RestoreTiles(List<ushort> canRestore) {
			ushort unloadedType = UnloadedType;
			for (int x = 0; x < Main.maxTilesX; x++) {
				for (int y = 0; y < Main.maxTilesY; y++) {
					if (Main.tile[x, y].type == unloadedType) {
						Tile tile = Main.tile[x, y];
						UnloadedTileFrame frame = new UnloadedTileFrame(tile.frameX, tile.frameY);
						int frameID = frame.FrameID;
						if (canRestore[frameID] > 0) {
							UnloadedTileInfo info = infos[frameID];
							tile.type = canRestore[frameID];
							tile.frameX = info.frameX;
							tile.frameY = info.frameY;
						}
					}
				}
			}
		}

		/// <summary>
		/// If there are pending tiles (after a mod disable), convert them to unloaded tiles, and refill <see cref="infos"/>
		/// </summary>
		private void ConfirmPendingInfo() {
			List<int> truePendingID = new List<int>();
			int nextID = 0;
			for (int k = 0; k < pendingInfos.Count; k++) {
				while (nextID < infos.Count && infos[nextID] != null)
					nextID++;

				if (nextID == infos.Count)
					infos.Add(pendingInfos[k]);
				else
					infos[nextID] = pendingInfos[k];

				truePendingID.Add(nextID);
			}
			ushort pendingType = PendingType;
			ushort unloadedType = UnloadedType;
			for (int x = 0; x < Main.maxTilesX; x++) {
				for (int y = 0; y < Main.maxTilesY; y++) {
					Tile tile = Main.tile[x, y];
					if (tile.type == pendingType) {
						UnloadedTileFrame frame = new UnloadedTileFrame(tile.frameX, tile.frameY);
						frame = new UnloadedTileFrame(truePendingID[frame.FrameID]);
						tile.type = unloadedType;
						tile.frameX = frame.FrameX;
						tile.frameY = frame.FrameY;
					}
				}
			}
		}
	}
}
