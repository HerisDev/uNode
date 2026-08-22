using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace MaxyGames.UNode.Editors {
	/// <summary>
	/// The kind of favorited item.
	/// </summary>
	public enum FavoriteKind {
		/// <summary>Favorited node (a node type registered in the node menu).</summary>
		Node = 0,
		/// <summary>Favorited type (native or uNode runtime type).</summary>
		Type = 1,
		/// <summary>Favorited member (field, property, method, constructor or event).</summary>
		Member = 2,
		/// <summary>A folder container. Can be nested and hold any item kind (including other folders).</summary>
		Folder = 3,
		/// <summary>A namespace. When expanded, it shows read-only virtual type rows derived from reflection.</summary>
		Namespace = 4,
	}

	/// <summary>
	/// ScriptableSingleton container for all favorites data.
	/// Persisted automatically by Unity inside Library/ScriptableSingletons
	/// (outside the Assets folder) using Unity's native serializer so that
	/// MemberData / SerializedType round-trip correctly.
	[FilePath(uNodePreference.preferenceDirectory + "/Favorites.asset", FilePathAttribute.Location.ProjectFolder)]
	/// </summary>
	public class FavoritesDataAsset : ScriptableSingleton<FavoritesDataAsset> {
		[Serializable]
		public class Category {
			public string id;
			public string name;
			public int orderIndex;
		}

		[Serializable]
		public class Entry {
			public string id;
			public FavoriteKind kind;
			public string categoryID;
			public int orderIndex;

			/// <summary>
			/// Parent entry id (null/empty = root of category).
			/// Only Folder and Namespace entries can be parents.
			/// </summary>
			public string parentID;

			/// <summary>
			/// True for rows generated from a namespace expansion (never persisted).
			/// </summary>
			public bool isVirtual;

			/// <summary>
			/// The targeted type. Used for Node and Type kinds.
			/// </summary>
			public SerializedType targetType;

			/// <summary>
			/// The targeted member. Used for the Member kind,
			/// its declaring type defines the group it belongs to.
			/// </summary>
			public MemberData targetMember;

			/// <summary>
			/// The node menu name, used for creating Node kind items on the graph.
			/// </summary>
			public string nodeMenuName;

			/// <summary>The display name (folder name or namespace string).</summary>
			public string displayName;

			/// <summary>Members excluded from this type favorite.</summary>
			public List<string> excludedMembers = new List<string>();

			/// <summary>
			/// The resolved System.Type of this entry (declaring type for members).
			/// Returns null for Folder, Namespace, and virtual entries.
			/// </summary>
			public Type resolvedType {
				get {
					if(isVirtual) return null;
					if(targetType != null && targetType.isAssigned)
						return targetType.type;
					if(targetMember != null)
						return targetMember.startType;
					return null;
				}
			}

			/// <summary>
			/// Full name of the group type this entry belongs to.
			/// </summary>
			public string typeName {
				get {
					var t = resolvedType;
					return t != null ? t.FullName : string.Empty;
				}
			}

			/// <summary>
			/// The member name for Member kind entries.
			/// </summary>
			public string memberName {
				get {
					if(kind != FavoriteKind.Member || targetMember == null)
						return null;
					if(!string.IsNullOrEmpty(targetMember.name))
						return targetMember.name;
					var members = targetMember.GetMembers(false);
					if(members != null && members.Length > 0)
						return members[members.Length - 1].Name;
					return null;
				}
			}

			public bool isValid {
				get {
					switch(kind) {
						case FavoriteKind.Member:
							return targetMember != null && !string.IsNullOrEmpty(memberName);
						case FavoriteKind.Folder:
						case FavoriteKind.Namespace:
							return !string.IsNullOrEmpty(displayName);
						default:
							return targetType != null && targetType.isAssigned;
					}
				}
			}

			/// <summary>True when this entry can contain child entries.</summary>
			public bool CanHaveChilds =>
				kind == FavoriteKind.Folder || kind == FavoriteKind.Namespace;

			/// <summary>True when this entry may receive drops from other items.</summary>
			public bool CanBeDropTarget =>
				kind == FavoriteKind.Folder;
		}

		public List<Category> categories = new List<Category>();
		public List<Entry> entries = new List<Entry>();

		/// <summary>
		/// Persist this singleton to disk.
		/// </summary>
		public void Save() {
			EditorUtility.SetDirty(this);
			base.Save(true);
		}
	}

	/// <summary>
	/// Static facade over the FavoritesDataAsset singleton providing
	/// CRUD operations, persistence and change notifications.
	/// </summary>
	public static class FavoritesManager {
		/// <summary>Raised whenever the favorites data changed.</summary>
		public static event Action onChanged;

		/// <summary>The underlying favorites data singleton.</summary>
		public static FavoritesDataAsset asset => FavoritesDataAsset.instance;

		static void RaiseChanged() {
			onChanged?.Invoke();
		}

		/// <summary>
		/// Raise the onChanged event (for external mutations).
		/// </summary>
		public static void NotifyChanged() {
			RaiseChanged();
		}

		#region Persistence
		static FavoritesManager() {
			EnsureDefaultCategory();
		}

		public static void Save() {
			asset.Save();
		}

		static void EnsureDefaultCategory() {
			if(asset.categories.Count == 0) {
				asset.categories.Add(new FavoritesDataAsset.Category {
					id = Guid.NewGuid().ToString(),
					name = "General",
					orderIndex = 0,
				});
			}
		}
		#endregion

		#region Categories
		public static FavoritesDataAsset.Category GetOrCreateCategory(string name) {
			var cat = asset.categories.FirstOrDefault(c => c.name == name);
			if(cat == null) {
				cat = new FavoritesDataAsset.Category {
					id = Guid.NewGuid().ToString(),
					name = name,
					orderIndex = asset.categories.Count,
				};
				asset.categories.Add(cat);
				Save();
				RaiseChanged();
			}
			return cat;
		}

		public static void RemoveCategory(string categoryID) {
			asset.categories.RemoveAll(c => c.id == categoryID);
			asset.entries.RemoveAll(e => e.categoryID == categoryID);
			Save();
			RaiseChanged();
		}

		public static void RenameCategory(string categoryID, string newName) {
			var cat = asset.categories.FirstOrDefault(c => c.id == categoryID);
			if(cat != null) {
				cat.name = newName;
				Save();
				RaiseChanged();
			}
		}

		public static List<FavoritesDataAsset.Category> GetCategories() {
			EnsureDefaultCategory();
			return asset.categories.OrderBy(c => c.orderIndex).ToList();
		}

		public static FavoritesDataAsset.Category GetDefaultCategory() {
			EnsureDefaultCategory();
			return asset.categories.OrderBy(c => c.orderIndex).First();
		}
		#endregion

		#region Entries
		/// <summary>
		/// Add an entry to the asset. Returns false and changes nothing when
		/// the entry is a duplicate member inside its type item.
		/// </summary>
		public static bool AddEntry(string categoryID, FavoritesDataAsset.Entry entry) {
			if(entry.parentID == null)
				entry.parentID = string.Empty;
			// Disallow duplicated members inside the same type item.
			if(entry.kind == FavoriteKind.Member) {
				var memberInfo = GetEntryMember(entry);
				bool duplicate = memberInfo != null
					? HasMember(categoryID, entry.parentID, memberInfo)
					: HasMemberByName(categoryID, entry.parentID, entry.memberName); // fallback when reflection fails
				if(duplicate)
					return false;
			}
			entry.categoryID = categoryID;
			entry.id = Guid.NewGuid().ToString();
			entry.orderIndex = NextOrderIndex(categoryID, entry.parentID);
			asset.entries.Add(entry);
			Save();
			RaiseChanged();
			return true;
		}

		/// <summary>
		/// Resolve the reflected MemberInfo targeted by a member entry
		/// (the last element of its MemberData chain).
		/// </summary>
		public static MemberInfo GetEntryMember(FavoritesDataAsset.Entry entry) {
			if(entry == null || entry.kind != FavoriteKind.Member || entry.targetMember == null)
				return null;
			try {
				var members = entry.targetMember.GetMembers(false);
				if(members != null && members.Length > 0)
					return members[members.Length - 1];
			}
			catch { }
			return null;
		}

		/// <summary>
		/// True when an equivalent member already exists under the same parent.
		/// Compares MemberInfo directly (reflection caches instances per member).
		/// </summary>
		public static bool HasMember(string categoryID, string parentID, MemberInfo member) {
			if(member == null)
				return false;
			return asset.entries.Any(e =>
				e.categoryID == categoryID &&
				e.kind == FavoriteKind.Member &&
				e.parentID == parentID &&
				GetEntryMember(e) == member);
		}

		/// <summary>Name-only fallback used when reflection resolution fails.</summary>
		public static bool HasMemberByName(string categoryID, string parentID, string memberName) {
			if(string.IsNullOrEmpty(memberName))
				return false;
			return asset.entries.Any(e =>
				e.categoryID == categoryID &&
				e.kind == FavoriteKind.Member &&
				e.parentID == parentID &&
				e.memberName == memberName);
		}

		public static void RemoveEntry(string entryID) {
			asset.entries.RemoveAll(e => e.id == entryID);
			Save();
			RaiseChanged();
		}

		/// <summary>
		/// Remove an entry and all its descendants (folder cascade).
		/// </summary>
		public static void RemoveRecursive(string entryID) {
			// Collect descendant ids iteratively (folder may contain nested folders).
			var toRemove = new HashSet<string> { entryID };
			bool added = true;
			while(added) {
				added = false;
				foreach(var e in asset.entries) {
					if(!string.IsNullOrEmpty(e.parentID) && toRemove.Contains(e.parentID) && toRemove.Add(e.id)) {
						added = true;
					}
				}
			}
			asset.entries.RemoveAll(e => toRemove.Contains(e.id));
			Save();
			RaiseChanged();
		}

		/// <summary>
		/// Rename a folder entry. Returns false if the entry is missing or not a folder.
		/// </summary>
		public static bool RenameFolder(string entryID, string newName) {
			if(string.IsNullOrWhiteSpace(newName)) return false;
			var entry = asset.entries.FirstOrDefault(e => e.id == entryID);
			if(entry == null || entry.kind != FavoriteKind.Folder)
				return false;
			entry.displayName = newName.Trim();
			Save();
			RaiseChanged();
			return true;
		}

		/// <summary>
		/// Move an entry to a new parent at a specific sibling index.
		/// Rejects cycle-creating moves (entry into its own descendant).
		/// </summary>
		public static bool MoveEntry(string entryID, string newParentID, int newSiblingIndex) {
			if(newParentID == null) newParentID = string.Empty;

			var entry = asset.entries.FirstOrDefault(e => e.id == entryID);
			if(entry == null) return false;

			// Cycle detection: newParentID must not be entryID nor a descendant of it.
			if(!string.IsNullOrEmpty(newParentID) && (newParentID == entryID || IsDescendantOf(newParentID, entryID))) {
				return false;
			}

			// Validate new parent: must be Folder in the same category (or empty for root).
			if(!string.IsNullOrEmpty(newParentID)) {
				var newParent = asset.entries.FirstOrDefault(e => e.id == newParentID);
				if(newParent == null || !newParent.CanBeDropTarget || newParent.categoryID != entry.categoryID) {
					return false;
				}
			}

			entry.parentID = newParentID;

			// Renumber sibling set at new location.
			var siblings = asset.entries
				.Where(e => e.categoryID == entry.categoryID && e.parentID == newParentID && e.id != entryID)
				.OrderBy(e => e.orderIndex)
				.ToList();
			if(newSiblingIndex < 0 || newSiblingIndex > siblings.Count) {
				newSiblingIndex = siblings.Count;
			}
			siblings.Insert(newSiblingIndex, entry);
			for(int i = 0; i < siblings.Count; i++) {
				siblings[i].orderIndex = i;
			}
			Save();
			RaiseChanged();
			return true;
		}

		static bool IsDescendantOf(string candidate, string ancestor) {
			var current = asset.entries.FirstOrDefault(e => e.id == candidate);
			while(current != null && !string.IsNullOrEmpty(current.parentID)) {
				if(current.parentID == ancestor) return true;
				current = asset.entries.FirstOrDefault(e => e.id == current.parentID);
			}
			return false;
		}

		static int NextOrderIndex(string categoryID, string parentID) {
			int max = -1;
			foreach(var e in asset.entries) {
				if(e.categoryID == categoryID && e.parentID == parentID && e.orderIndex > max) {
					max = e.orderIndex;
				}
			}
			return max + 1;
		}

		/// <summary>
		/// Add a folder entry. Inserts at the end of the given parent (or root if parentID is null/empty).
		/// </summary>
		public static FavoritesDataAsset.Entry AddFolder(string categoryID, string name, string parentID = null) {
			var entry = new FavoritesDataAsset.Entry {
				kind = FavoriteKind.Folder,
				displayName = name,
			};
			AddEntry(categoryID, entry);
			return entry;
		}

		/// <summary>
		/// Add a namespace entry.
		/// </summary>
		public static FavoritesDataAsset.Entry AddNamespace(string categoryID, string @namespace, string parentID = null) {
			var entry = new FavoritesDataAsset.Entry {
				kind = FavoriteKind.Namespace,
				displayName = @namespace,
			};
			AddEntry(categoryID, entry);
			return entry;
		}

		/// <summary>
		/// Get all entries that are direct children of the given parent (or root).
		/// </summary>
		public static List<FavoritesDataAsset.Entry> GetChildren(string categoryID, string parentID) {
			var pid = parentID ?? string.Empty;
			return asset.entries
				.Where(e => e.categoryID == categoryID && e.parentID == pid)
				.OrderBy(e => e.orderIndex)
				.ToList();
		}

		/// <summary>
		/// Return the root-level entries of a category.
		/// </summary>
		public static List<FavoritesDataAsset.Entry> GetEntriesForCategory(string categoryID) {
			return GetChildren(categoryID, string.Empty);
		}

		/// <summary>
		/// Reorder a sibling set by replacing orderIndex after sorting.
		/// </summary>
		public static void ReorderSiblings(string categoryID, string parentID, Comparison<FavoritesDataAsset.Entry> comparer) {
			var siblings = GetChildren(categoryID, parentID);
			siblings.Sort((a, b) => comparer(a, b));
			for(int i = 0; i < siblings.Count; i++) {
				siblings[i].orderIndex = i;
			}
			Save();
			RaiseChanged();
		}

		/// <summary>
		/// Read-only: generate virtual type entries for the given namespace by reflecting over
		/// loaded assemblies. These are never persisted.
		/// </summary>
		public static List<FavoritesDataAsset.Entry> GetVirtualNamespaceChildren(string @namespace) {
			var result = new List<FavoritesDataAsset.Entry>();
			if(string.IsNullOrEmpty(@namespace)) return result;
			foreach(var asm in AppDomain.CurrentDomain.GetAssemblies()) {
				Type[] types;
				try { types = asm.GetTypes(); }
				catch { continue; }
				foreach(var t in types) {
					if(t == null || t.IsNested || t.Namespace != @namespace) continue;
					if(t.IsSpecialName || t.Name.Contains('<') || t.Name.StartsWith("__")) continue;
					result.Add(new FavoritesDataAsset.Entry {
						id = "[ns]:" + t.AssemblyQualifiedName,
						kind = FavoriteKind.Type,
						targetType = new SerializedType(t),
						isVirtual = true,
						parentID = "[ns]:" + @namespace,
					});
				}
			}
			result.Sort((a, b) => string.Compare(a.targetType.type.FullName, b.targetType.type.FullName, StringComparison.OrdinalIgnoreCase));
			return result;
		}
		#endregion
	}
}
