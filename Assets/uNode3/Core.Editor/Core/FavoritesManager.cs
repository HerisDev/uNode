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
	/// How a type item's generated member list behaves.
	/// The stored name list flips meaning with the mode:
	/// IncludeAll → names are hidden members; ExcludeAll → names are visible members.
	/// </summary>
	public enum TypeMemberMode {
		/// <summary>All generated members are shown unless excluded by name.</summary>
		IncludeAll = 0,
		/// <summary>No generated members are shown unless included by name.</summary>
		ExcludeAll = 1,
	}

	/// <summary>
	/// ScriptableSingleton container for all favorites data.
	/// Persisted automatically by Unity inside Library/ScriptableSingletons
	/// (outside the Assets folder) using Unity's native serializer so that
	/// SerializedType references round-trip correctly. Members are not
	/// persisted — they are generated from their type items via reflection.
	/// The hierarchy is stored nested: each category holds its root entries,
	/// and folders/namespaces embed their children.
	/// </summary>
	[FilePath(uNodePreference.preferenceDirectory + "/Favorites.asset", FilePathAttribute.Location.ProjectFolder)]
	public class FavoritesDataAsset : ScriptableSingleton<FavoritesDataAsset> {
		[Serializable]
		public class FavoriteCategory {
			public string id;
			public string name;
			[SerializeReference]
			public List<FavoriteEntry> roots = new List<FavoriteEntry>();
		}

		[Serializable]
		public class FavoriteEntry {
			public string id;
			public FavoriteKind kind;
			public string displayName;

			/// <summary>The targeted type. Used for Type kind.</summary>
			public SerializedType targetType;

			/// <summary>The node menu name. Used for Node kind.</summary>
			public string nodeMenuName;

			/// <summary>
			/// Member/type names list. Meaning flips with memberMode:
			/// hidden members/types in IncludeAll, visible ones in ExcludeAll.
			/// </summary>
			public List<string> excludedMembers = new List<string>();

			/// <summary>How the generated list behaves (see TypeMemberMode).</summary>
			public TypeMemberMode memberMode = TypeMemberMode.IncludeAll;

			/// <summary>
			/// True for rows generated at runtime (namespace types / type members /
			/// deep search results). Never serialized.
			/// </summary>
			public bool isVirtual;

			/// <summary>Embedded children. Only meaningful for Folder/Namespace entries.</summary>
			[SerializeReference]
			public List<FavoriteEntry> children = new List<FavoriteEntry>();

			/// <summary>
			/// Runtime-only reflected member for virtual Member entries.
			/// Never serialized — open generics stay intact.
			/// </summary>
			[System.NonSerialized]
			public MemberInfo rawMember;

			/// <summary>Runtime back-reference to the containing entry/category root.</summary>
			[System.NonSerialized]
			public FavoriteEntry parentEntry;

			/// <summary>Runtime back-reference for virtual rows: the favorited owner.</summary>
			[System.NonSerialized]
			public FavoriteEntry ownerEntry;

			/// <summary>
			/// The resolved System.Type of this entry (declaring type for members).
			/// Returns null for Folder/Namespace and virtual entries.
			/// </summary>
			public Type resolvedType {
				get {
					if(isVirtual) return null;
					if(targetType != null && targetType.isAssigned)
						return targetType.type;
					if(rawMember != null)
						return rawMember is Type rt ? rt : rawMember.DeclaringType;
					return null;
				}
			}

			/// <summary>Full name of the group type this entry belongs to.</summary>
			public string typeName {
				get {
					var t = resolvedType;
					return t != null ? t.FullName : string.Empty;
				}
			}

			/// <summary>The reflected member name for Member kind entries.</summary>
			public string memberName {
				get {
					if(kind != FavoriteKind.Member)
						return null;
					return rawMember?.Name;
				}
			}

			public bool isValid {
				get {
					switch(kind) {
						case FavoriteKind.Member:
							return rawMember != null && !string.IsNullOrEmpty(memberName);
						case FavoriteKind.Folder:
						case FavoriteKind.Namespace:
							return !string.IsNullOrEmpty(displayName);
						default:
							return targetType != null && targetType.isAssigned || kind == FavoriteKind.Node && !string.IsNullOrEmpty(nodeMenuName);
					}
				}
			}

			/// <summary>True when this entry can contain serialized child entries.</summary>
			public bool CanHaveChilds =>
				kind == FavoriteKind.Folder || kind == FavoriteKind.Namespace;

			/// <summary>True when this entry may receive drops from other items.</summary>
			public bool CanBeDropTarget => kind == FavoriteKind.Folder;
		}

		public List<FavoriteCategory> categories = new List<FavoriteCategory>();

		/// <summary>Entry ids whose tree row is currently expanded (persisted).</summary>
		public List<string> expandedEntries = new List<string>();

		public void Save() {
			EditorUtility.SetDirty(this);
			base.Save(true);
		}
	}

	/// <summary>
	/// Static facade over the FavoritesDataAsset singleton providing tree CRUD,
	/// persistence and change notifications.
	/// </summary>
	public static class FavoritesManager {
		/// <summary>Raised whenever the favorites data changed.</summary>
		public static event Action onChanged;

		public static FavoritesDataAsset asset => FavoritesDataAsset.instance;

		static bool s_Initialized;

		static void EnsureInitialized() {
			if(s_Initialized)
				return;
			s_Initialized = true;
			EnsureSeeded();
		}

		static void RaiseChanged() {
			onChanged?.Invoke();
		}

		/// <summary>Raise the onChanged event (for external mutations).</summary>
		public static void NotifyChanged() {
			RaiseChanged();
		}

		public static void Save() {
			EnsureInitialized();
			asset.Save();
		}

		#region First Run
		/// <summary>Creates the default category pre-populated with useful namespaces.</summary>
		static void EnsureSeeded() {
			if(asset.categories.Count > 0) {
				foreach(var cat in asset.categories)
					RefreshParents(cat);
				return;
			}
			var general = new FavoritesDataAsset.FavoriteCategory {
				id = Guid.NewGuid().ToString(),
				name = "General",
			};
			asset.categories.Add(general);
			SeedDefaultNamespaces(general);
			Save();
		}

		/// <summary>
		/// Pre-populates a freshly created category with commonly used namespaces,
		/// mirroring uNode's default favorite namespaces plus a few essentials.
		/// </summary>
		static void SeedDefaultNamespaces(FavoritesDataAsset.FavoriteCategory category) {
			string[] defaultNamespaces = {
				"System",
				"System.Collections",
				"System.Collections.Generic",
				"UnityEngine",
				"UnityEngine.AI",
				"UnityEngine.Events",
				"UnityEngine.EventSystems",
				"UnityEngine.SceneManagement",
				"UnityEngine.UI",
				"UnityEngine.UIElements",
			};
			foreach(var ns in defaultNamespaces) {
				category.roots.Add(new FavoritesDataAsset.FavoriteEntry {
					id = Guid.NewGuid().ToString(),
					kind = FavoriteKind.Namespace,
					displayName = ns,
				});
			}
		}
		#endregion

		#region Categories
		public static List<FavoritesDataAsset.FavoriteCategory> GetCategories() {
			EnsureInitialized();
			return asset.categories;
		}

		public static FavoritesDataAsset.FavoriteCategory GetDefaultCategory() {
			EnsureInitialized();
			return asset.categories.FirstOrDefault() ?? GetOrCreateCategory("General");
		}

		public static FavoritesDataAsset.FavoriteCategory GetOrCreateCategory(string name) {
			EnsureInitialized();
			var cat = asset.categories.FirstOrDefault(c => c.name == name);
			if(cat == null) {
				cat = new FavoritesDataAsset.FavoriteCategory {
					id = Guid.NewGuid().ToString(),
					name = name,
				};
				asset.categories.Add(cat);
				Save();
				RaiseChanged();
			}
			return cat;
		}

		public static void RemoveCategory(FavoritesDataAsset.FavoriteCategory category) {
			if(category == null) return;
			foreach(var id in Flatten(category).Select(e => e.id))
				SetExpandedInternal(id, false);
			asset.categories.Remove(category);
			Save();
			RaiseChanged();
		}

		public static void RenameCategory(FavoritesDataAsset.FavoriteCategory category, string newName) {
			if(category == null || string.IsNullOrWhiteSpace(newName)) return;
			category.name = newName.Trim();
			Save();
			RaiseChanged();
		}
		#endregion

		#region Tree Access
		/// <summary>Depth-first iteration over every persisted entry of a category.</summary>
		public static IEnumerable<FavoritesDataAsset.FavoriteEntry> Flatten(FavoritesDataAsset.FavoriteCategory category) {
			if(category == null)
				yield break;
			foreach(var root in category.roots) {
				foreach(var e in Flatten(root))
					yield return e;
			}
		}

		static IEnumerable<FavoritesDataAsset.FavoriteEntry> Flatten(FavoritesDataAsset.FavoriteEntry entry) {
			if(entry == null)
				yield break;
			yield return entry;
			if(entry.CanHaveChilds) {
				foreach(var child in entry.children) {
					foreach(var e in Flatten(child))
						yield return e;
				}
			}
		}

		/// <summary>Depth-first iteration over all persisted entries of all categories.</summary>
		public static IEnumerable<FavoritesDataAsset.FavoriteEntry> FlattenAll() {
			EnsureInitialized();
			foreach(var cat in asset.categories) {
				foreach(var e in Flatten(cat))
					yield return e;
			}
		}

		public static FavoritesDataAsset.FavoriteEntry FindEntry(string entryID) {
			if(string.IsNullOrEmpty(entryID))
				return null;
			return FlattenAll().FirstOrDefault(e => e.id == entryID);
		}

		/// <summary>(Re)assigns runtime parent references for a whole category.</summary>
		public static void RefreshParents(FavoritesDataAsset.FavoriteCategory category) {
			if(category == null) return;
			foreach(var root in category.roots)
				RefreshParents(root, null);
		}

		static void RefreshParents(FavoritesDataAsset.FavoriteEntry entry, FavoritesDataAsset.FavoriteEntry parent) {
			if(entry == null) return;
			entry.parentEntry = parent;
			if(entry.CanHaveChilds) {
				foreach(var c in entry.children)
					RefreshParents(c, entry);
			}
		}

		/// <summary>True when ancestor is entry itself or any of its parents.</summary>
		public static bool IsDescendantOf(FavoritesDataAsset.FavoriteEntry entry, FavoritesDataAsset.FavoriteEntry ancestor) {
			var cur = entry;
			while(cur != null) {
				if(ReferenceEquals(cur, ancestor))
					return true;
				cur = cur.parentEntry;
			}
			return false;
		}
		#endregion

		#region CRUD
		/// <summary>
		/// Insert an entry under parent (null = category root). Assigns an id,
		/// sets the runtime parent link, persists, and notifies.
		/// </summary>
		public static FavoritesDataAsset.FavoriteEntry AddEntry(
			FavoritesDataAsset.FavoriteCategory category,
			FavoritesDataAsset.FavoriteEntry parent,
			FavoritesDataAsset.FavoriteEntry entry) {
			EnsureInitialized();
			if(category == null || entry == null)
				return entry;
			if(string.IsNullOrEmpty(entry.id))
				entry.id = Guid.NewGuid().ToString();
			var container = parent != null ? parent.children : category.roots;
			container.Add(entry);
			entry.parentEntry = parent;
			Save();
			RaiseChanged();
			return entry;
		}

		/// <summary>
		/// Remove an entry from its container. Children are removed with it
		/// automatically since they live inside the entry.
		/// </summary>
		public static void Remove(FavoritesDataAsset.FavoriteEntry entry) {
			EnsureInitialized();
			if(entry == null) return;
			if(!Detach(entry)) return;
			SetExpandedInternal(entry.id, false);
			Save();
			RaiseChanged();
		}

		/// <summary>Detaches an entry from its container; returns false when not found.</summary>
		static bool Detach(FavoritesDataAsset.FavoriteEntry entry) {
			if(entry.parentEntry != null)
				return entry.parentEntry.children.Remove(entry);
			foreach(var cat in asset.categories) {
				if(cat.roots.Remove(entry)) {
					RefreshParents(cat);
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Move an entry under newParent (null = category root) at the given
		/// sibling index (-1 = append). Validates: folder-only parent, same
		/// category implied by object graph, no self/descendant moves.
		/// </summary>
		public static bool Move(FavoritesDataAsset.FavoriteEntry entry,
			FavoritesDataAsset.FavoriteEntry newParent, int index, FavoritesDataAsset.FavoriteCategory category) {
			EnsureInitialized();
			if(entry == null || category == null)
				return false;
			if(newParent != null && !newParent.CanBeDropTarget)
				return false;
			if(IsDescendantOf(newParent, entry)) // would create a cycle
				return false;
			if(ReferenceEquals(entry.parentEntry, newParent) &&
				(entry.parentEntry != null || category.roots.Contains(entry))) {
				// Same container — pure reorder below handles it; still validate bounds.
			}
			if(index < -1)
				index = -1;
			var sourceList = entry.parentEntry != null ? entry.parentEntry.children : category.roots;
			if(!sourceList.Contains(entry)) {
				// Stale runtime links — refresh and retry once.
				RefreshParents(category);
				sourceList = entry.parentEntry != null ? entry.parentEntry.children : category.roots;
				if(!sourceList.Contains(entry))
					return false;
			}
			var targetList = newParent != null ? newParent.children : category.roots;
			int insertAt = index < 0 || index >= targetList.Count ? targetList.Count : index;
			if(ReferenceEquals(sourceList, targetList)) {
				int oldIndex = sourceList.IndexOf(entry);
				if(oldIndex == insertAt || oldIndex == insertAt - 1)
					return true; // already in place
			}
			DetachSilent(entry);
			insertAt = Mathf.Clamp(insertAt, 0, targetList.Count);
			targetList.Insert(insertAt, entry);
			entry.parentEntry = newParent;
			Save();
			RaiseChanged();
			return true;
		}

		static void DetachSilent(FavoritesDataAsset.FavoriteEntry entry) {
			if(entry.parentEntry != null)
				entry.parentEntry.children.Remove(entry);
			else {
				foreach(var cat in asset.categories)
					cat.roots.Remove(entry);
			}
		}

		/// <summary>Sort a container's direct children with the given comparison.</summary>
		public static void SortChildren(FavoritesDataAsset.FavoriteEntry parent,
			FavoritesDataAsset.FavoriteCategory category, Comparison<FavoritesDataAsset.FavoriteEntry> comparison) {
			EnsureInitialized();
			var list = parent != null ? parent.children : category.roots;
			if(list == null || list.Count < 2)
				return;
			list.Sort(comparison);
			Save();
			RaiseChanged();
		}

		public static void Rename(FavoritesDataAsset.FavoriteEntry entry, string newName) {
			if(entry == null || string.IsNullOrWhiteSpace(newName)) return;
			entry.displayName = newName.Trim();
			Save();
			RaiseChanged();
		}
		#endregion

		#region Expanded State
		/// <summary>True when the given entry's tree row is persisted as expanded.</summary>
		public static bool IsEntryExpanded(string entryID) {
			return !string.IsNullOrEmpty(entryID) && asset.expandedEntries.Contains(entryID);
		}

		public static void SetEntryExpanded(string entryID, bool expanded) {
			if(string.IsNullOrEmpty(entryID))
				return;
			bool changed;
			if(expanded) {
				changed = !asset.expandedEntries.Contains(entryID);
				if(changed)
					asset.expandedEntries.Add(entryID);
			}
			else {
				changed = asset.expandedEntries.Remove(entryID);
			}
			if(changed)
				Save();
		}

		static void SetExpandedInternal(string entryID, bool expanded) {
			if(string.IsNullOrEmpty(entryID))
				return;
			if(expanded)
				asset.expandedEntries.Add(entryID);
			else
				asset.expandedEntries.Remove(entryID);
		}
		#endregion

		#region Visibility Rules
		/// <summary>
		/// Mode-aware visibility of a reflected member under a type favorite.
		/// IncludeAll → visible unless listed; ExcludeAll → visible only when
		/// listed. A null owner is always visible.
		/// </summary>
		public static bool IsMemberVisibleIn(FavoritesDataAsset.FavoriteEntry typeEntry, MemberInfo member) {
			if(typeEntry == null || member == null)
				return true;
			bool inList = typeEntry.excludedMembers != null && typeEntry.excludedMembers.Contains(member.Name);
			return typeEntry.memberMode == TypeMemberMode.ExcludeAll ? inList : !inList;
		}

		/// <summary>
		/// Mode-aware visibility of a type name under a namespace favorite.
		/// Mirrors IsMemberVisibleIn. A null owner is always visible.
		/// </summary>
		public static bool IsTypeNameVisibleIn(FavoritesDataAsset.FavoriteEntry nsEntry, string typeName) {
			if(nsEntry == null || string.IsNullOrEmpty(typeName))
				return true;
			bool inList = nsEntry.excludedMembers != null && nsEntry.excludedMembers.Contains(typeName);
			return nsEntry.memberMode == TypeMemberMode.ExcludeAll ? inList : !inList;
		}

		/// <summary>True for compiler-generated property accessors (get_/set_).</summary>
		public static bool IsAccessorMethod(MemberInfo member) {
			return member is MethodInfo method &&
				(method.Name.StartsWith("get_", StringComparison.Ordinal) ||
				 method.Name.StartsWith("set_", StringComparison.Ordinal));
		}
		#endregion

		#region Virtual Generation
		/// <summary>
		/// Resolve the reflected MemberInfo of a member entry. Virtual entries
		/// carry the raw MemberInfo directly (open generics stay intact).
		/// </summary>
		public static MemberInfo GetEntryMember(FavoritesDataAsset.FavoriteEntry entry) {
			if(entry == null || entry.kind != FavoriteKind.Member)
				return null;
			return entry.rawMember;
		}

		/// <summary>
		/// Read-only: generate virtual type entries for the given namespace favorite.
		/// When ignoreVisibility is false the namespace's memberMode + excludedMembers
		/// list filter which types are returned. These are never persisted.
		/// </summary>
		public static List<FavoritesDataAsset.FavoriteEntry> GetVirtualNamespaceChildren(
			FavoritesDataAsset.FavoriteEntry nsEntry, bool ignoreVisibility = false) {
			var result = new List<FavoritesDataAsset.FavoriteEntry>();
			if(nsEntry == null || string.IsNullOrEmpty(nsEntry.displayName))
				return result;
			foreach(var asm in AppDomain.CurrentDomain.GetAssemblies()) {
				Type[] types;
				try { types = asm.GetTypes(); }
				catch { continue; }
				foreach(var t in types) {
					if(t == null || t.IsNested || t.Namespace != nsEntry.displayName) continue;
					if(t.IsSpecialName || t.Name.Contains('<') || t.Name.StartsWith("__")) continue;
					if(!ignoreVisibility && !IsTypeNameVisibleIn(nsEntry, t.Name))
						continue;
					result.Add(new FavoritesDataAsset.FavoriteEntry {
						id = "[ns]:" + t.AssemblyQualifiedName,
						kind = FavoriteKind.Type,
						targetType = new SerializedType(t),
						isVirtual = true,
						displayName = t.Name,
						ownerEntry = nsEntry,
						parentEntry = nsEntry,
					});
				}
			}
			result.Sort((a, b) => string.Compare(a.targetType.type.FullName, b.targetType.type.FullName, StringComparison.OrdinalIgnoreCase));
			return result;
		}

		/// <summary>
		/// Read-only: generate virtual member entries for the given type favorite.
		/// Visibility is driven by memberMode + excludedMembers. Never persisted.
		/// </summary>
		public static List<FavoritesDataAsset.FavoriteEntry> GetVirtualTypeMembers(FavoritesDataAsset.FavoriteEntry typeEntry) {
			var result = new List<FavoritesDataAsset.FavoriteEntry>();
			if(typeEntry == null || typeEntry.kind != FavoriteKind.Type || typeEntry.isVirtual)
				return result;
			Type type = null;
			try { type = typeEntry.resolvedType; } catch { }
			if(type == null || type.IsEnum)
				return result;
			MemberInfo[] members;
			try {
				members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
			}
			catch {
				return result;
			}
			string declName = type.FullName ?? type.Name;
			foreach(var m in members) {
				if(m is EventInfo) continue;
				if(m is ConstructorInfo ctor && ctor.GetParameters().Length > 6) continue;
				if(IsAccessorMethod(m)) continue;
				if(!IsMemberVisibleIn(typeEntry, m))
					continue;
				result.Add(new FavoritesDataAsset.FavoriteEntry {
					id = "[member]:" + declName + "::" + m.Name + "::" + m.MetadataToken,
					kind = FavoriteKind.Member,
					rawMember = m,
					isVirtual = true,
					displayName = m.Name,
					ownerEntry = typeEntry,
					parentEntry = typeEntry,
				});
			}
			result.Sort((a, b) => string.Compare(a.memberName, b.memberName, StringComparison.OrdinalIgnoreCase));
			return result;
		}
		#endregion
	}
}
