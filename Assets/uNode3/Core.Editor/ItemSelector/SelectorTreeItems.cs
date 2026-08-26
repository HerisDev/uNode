using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityObject = UnityEngine.Object;

#if UNITY_6000_2_OR_NEWER
using TViewItem = UnityEditor.IMGUI.Controls.TreeViewItem<int>;
#else
using TViewItem = UnityEditor.IMGUI.Controls.TreeViewItem;
#endif

namespace MaxyGames.UNode.Editors {
	#region TreeViews
	// Shared reflection-tree item classes used by the ItemSelector.
	// Originally defined in NodeBrowser; kept here after its removal.

	internal interface IDisplayName {
		string DisplayName { get; }
	}

	public interface ISelectorItem { }

	public interface ISelectorItemWithValue : ISelectorItem {
		public object ItemValue { get; }
	}

	public interface ISelectorItemWithType : ISelectorItem {
		public Type ItemType { get; }
	}

	public class TypeTreeView : MemberTreeView {
		public Type type;
		public FilterAttribute filter;

		private List<MemberTreeView> members;

		internal void Search(Func<MemberInfo, float> scoring) {
			children = new List<TViewItem>(ItemSelector.TreeFunction.CreateItemsFromType(type, filter, true, scoring));
		}

		public void Expand(bool enable) {
			if(enable) {
				if(members == null) {
					members = ItemSelector.TreeFunction.CreateItemsFromType(type, filter, true);
				}
				if(members != null) {
					children = new List<TViewItem>(members);
				}
			} else {
				children = null;
			}
		}

		public TypeTreeView() {

		}

		public TypeTreeView(Type type) : base(type, type.GetHashCode(), -1) {
			this.type = type;
			member = type;
		}

		public TypeTreeView(Type type, int id, int depth) : base(type, id, depth) {
			this.type = type;
			member = type;
		}

		public static TypeTreeView Create(Type type) {
			return new TypeTreeView(type, uNodeEditorUtility.GetUIDFromString(type.FullName), -1);
		}
	}

	internal class NamespaceTreeView : TViewItem, ISelectorItem {
		public string Namespace;

		public NamespaceTreeView() {

		}

		public NamespaceTreeView(string Namespace, int id, int depth) : base(id, depth, Namespace) {
			this.Namespace = Namespace;
		}
	}

	public class MemberTreeView : TViewItem, IDisplayName, ISelectorItemWithValue, ISelectorItemWithType, IRelevanceItem {
		public MemberInfo member;
		public object instance;

		public Func<bool> selectValidation;
		public Func<bool> nextValidation;

		public string DisplayName {
			get {
				if(member is MethodInfo method) {
					if(method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)) {
						return EditorReflectionUtility.GetPrettyExtensionMethodName(method);
					}
				}
				if(uNodePreference.preferenceData.coloredItem) {
					return EditorReflectionUtility.GetRichMemberName(member);
				}
				else {
					return EditorReflectionUtility.GetPrettyMemberName(member);
				}
			}
		}

		public object ItemValue => member;

		public Type ItemType => ReflectionUtils.GetMemberType(member);

		public float Score { get; set; }

		public MemberTreeView() {

		}

		public MemberTreeView(MemberInfo member) : base(member.GetHashCode(), -1, EditorReflectionUtility.GetMemberName(member)) {
			this.member = member;
		}

		public MemberTreeView(MemberInfo member, int id, int depth) : base(id, depth, EditorReflectionUtility.GetMemberName(member)) {
			this.member = member;
		}

		public Texture GetIcon() {
			if(member is Type) {
				return uNodeEditorUtility.GetTypeIcon(member as Type);
			}
			return uNodeEditorUtility.GetIcon(member);
		}

		public bool CanSelect() {
			return selectValidation == null || selectValidation();
		}

		public bool HasDeepMember() {
			if(nextValidation != null) {
				return nextValidation();
			}
			return EditorReflectionUtility.ValidateNextMember(member, FilterAttribute.Default);
		}
	}

	internal class NodeTreeView : TViewItem, ISelectorItemWithValue, IRelevanceItem {
		public SelectorItemNodeTreeData data;

		public NodeTreeView() {

		}

		public NodeTreeView(SelectorItemNodeTreeData data, int id, int depth) : base(id, depth, data.name) {
			this.data = data;
		}

		public object ItemValue => data;

		public float Score { get; set; }
	}

	public class SelectorItemNodeTreeData {
		public string name;
		public NodeMenu menu;
		public INodeItemCommand command;
		public string category;
	}
	#endregion
}
