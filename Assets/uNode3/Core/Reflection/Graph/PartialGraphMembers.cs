using System;
using System.Collections.Generic;

namespace MaxyGames.UNode {
	/// <summary>
	/// The kind of member declared in the hand-written half of a partial graph.
	/// </summary>
	public enum PartialMemberKind {
		Field,
		Property,
		Method,
	}

	/// <summary>
	/// Describes a single parameter of a <see cref="PartialMemberInfo"/>.
	/// </summary>
	public class PartialParameterInfo {
		public string name;
		public Type type;
		public RefKind refKind;
		public bool hasDefaultValue;
		public object defaultValue;
	}

	/// <summary>
	/// Describes a member that exists in the hand-written half of a `partial` graph.
	/// These are discovered from the project source, they are not authored in the graph.
	/// </summary>
	public class PartialMemberInfo {
		public PartialMemberKind kind;
		public string name;
		/// <summary>
		/// The field/property type, or the return type of a method.
		/// </summary>
		public Type type;
		public PartialParameterInfo[] parameters = Array.Empty<PartialParameterInfo>();
		public bool isStatic;
		public bool isPublic = true;
		public bool canRead = true;
		public bool canWrite = true;
		public string summary;

		/// <summary>
		/// The source file this member was found in, for diagnostics.
		/// </summary>
		public string sourcePath;

		public Type[] ParameterTypes() {
			if(parameters == null || parameters.Length == 0)
				return Type.EmptyTypes;
			var result = new Type[parameters.Length];
			for(int i = 0; i < parameters.Length; i++) {
				result[i] = parameters[i].type;
			}
			return result;
		}

		/// <summary>
		/// A signature usable for de-duplicating against graph-authored members.
		/// </summary>
		public string Signature() {
			if(kind != PartialMemberKind.Method)
				return name;
			var result = name + "(";
			for(int i = 0; i < parameters.Length; i++) {
				if(i != 0)
					result += ",";
				result += parameters[i].type != null ? parameters[i].type.FullName : "?";
			}
			return result + ")";
		}
	}

	/// <summary>
	/// The seam between the runtime reflection types (this assembly) and the other half of a
	/// `partial` graph. Two views are available: <see cref="Get"/> for the syntax-scanned
	/// member descriptions, and <see cref="GetOtherHalfType"/> for the real compiled CLR type
	/// of the hand-written half, which is what the merged graph type reflects over.
	/// The editor installs both providers on load; in a build they stay null and the type is
	/// resolved by full name instead, because the hand-written script compiles with the project.
	/// </summary>
	public static class PartialGraphMembers {
		private static readonly PartialMemberInfo[] none = Array.Empty<PartialMemberInfo>();

		/// <summary>
		/// Installed by the editor. Returns the members declared in the hand-written
		/// half of the given graph, or null when the graph is not partial.
		/// </summary>
		public static Func<GraphAsset, IList<PartialMemberInfo>> provider;

		/// <summary>
		/// Installed by the editor. Returns the compiled CLR type of the hand-written half,
		/// or null when the graph has no other half (which is not an error).
		/// </summary>
		public static Func<GraphAsset, Type> typeProvider;

		/// <summary>
		/// Installed by the editor. Returns every other reflection type sharing this graph's
		/// full name, ie. the other `partial` graph halves of the same class.
		/// </summary>
		public static Func<GraphAsset, IList<RuntimeType>> siblingProvider;

		/// <summary>
		/// Installed by the editor. Returns the one <see cref="RuntimePartialGraphType"/>
		/// shared by all halves under this graph's full name, or null when unknown.
		/// Receives the caller's own raw instance so a fresh registry entry can be created
		/// when none exists yet.
		/// </summary>
		public static Func<IGraph, RuntimeType, RuntimeType> partialTypeProvider;

		/// <summary>
		/// The owning graph asset behind a runtime graph type, regardless of which wrapper
		/// kind presents it (`RuntimeGraphType`, `RuntimePartialGraphType` or legacy native
		/// wrappers).
		/// </summary>
		public static GraphAsset GetOwnerAsset(RuntimeType type) {
			if(type is IPartialGraphType partial)
				return partial.ownerAsset;
			if(type is RuntimeGraphType graphType)
				return graphType.target;
			if(type is RuntimeNativeGraph nativeGraph)
				return nativeGraph.target;
			return null;
		}

		/// <summary>
		/// The reflection type every consumer should see for the given partial graph: the one
		/// combined <see cref="RuntimePartialGraphType"/> under its full name. Returns `own`
		/// untouched for non-partial graphs or when no provider answered.
		/// </summary>
		public static RuntimeType GetReflectionType(IGraph self, RuntimeType own) {
			if(partialTypeProvider == null || self == null)
				return own;
			var modifier = self as IClassModifier;
			if(modifier == null || modifier.GetModifier()?.Partial != true)
				return own;
			try {
				return partialTypeProvider(self, own) ?? own;
			}
			catch(Exception ex) {
				UnityEngine.Debug.LogException(ex);
				return own;
			}
		}

		public static IList<PartialMemberInfo> Get(GraphAsset graph) {
			if(provider == null || graph == null)
				return none;
			try {
				return provider(graph) ?? none;
			}
			catch(Exception ex) {
				//Never let a scanner failure break reflection on the graph.
				UnityEngine.Debug.LogException(ex);
				return none;
			}
		}

		/// <summary>
		/// The real CLR type behind the hand-written half of a `partial` graph, or null when
		/// the graph has none. A null result is normal: being `partial` no longer requires a
		/// second declaration to exist anywhere.
		/// </summary>
		public static Type GetOtherHalfType(GraphAsset graph) {
			if(graph == null)
				return null;
			try {
				if(typeProvider != null)
					return typeProvider(graph);
				//In a build there is no source scanner, but the hand-written script compiled
				//with the project, so the class is reachable by its generated full name.
				var modifier = graph as IClassModifier;
				if(modifier == null || modifier.GetModifier()?.Partial == false)
					return null;
				var name = graph.GetGraphName();
				if(string.IsNullOrEmpty(name))
					return null;
				var ns = graph.GetGraphNamespace() ?? string.Empty;
				return (string.IsNullOrEmpty(ns) ? name : ns + "." + name).ToType(false);
			}
			catch(Exception ex) {
				UnityEngine.Debug.LogException(ex);
				return null;
			}
		}

		/// <summary>
		/// The other graph halves of the same class: every partial reflection type whose full
		/// name equals this graph's. Empty when the graph is alone under its name, and in builds,
		/// where duplicate names cannot exist because the compiler would reject the merge.
		/// </summary>
		public static IList<RuntimeType> GetSiblingReflectionTypes(GraphAsset graph) {
			if(siblingProvider == null || graph == null)
				return Array.Empty<RuntimeType>();
			try {
				return siblingProvider(graph) ?? Array.Empty<RuntimeType>();
			}
			catch(Exception ex) {
				UnityEngine.Debug.LogException(ex);
				return Array.Empty<RuntimeType>();
			}
		}
	}
}
