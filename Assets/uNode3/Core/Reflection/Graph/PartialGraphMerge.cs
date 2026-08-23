using System;
using System.Collections.Generic;
using System.Reflection;

namespace MaxyGames.UNode {
	/// <summary>
	/// The one place where members of a `partial` graph's other halves are merged into a
	/// runtime type: the compiled hand-written class when it exists, plus every sibling
	/// graph sharing the same full name. Both interpreted (`RuntimeGraphType`) and native
	/// backed (`RuntimeNativeGraph`) types route through here, so they always combine
	/// identically and independently of whether a compiled other half exists yet.
	/// Graph-authored members always win: nothing here shadows an existing entry.
	/// </summary>
	internal static class PartialGraphMerge {
		public static void AppendExternalFields(GraphAsset self, RuntimeType owner, List<FieldInfo> fields) {
			var otherHalf = PartialGraphMembers.GetOtherHalfType(self);
			if(otherHalf != null) {
				foreach(var native in otherHalf.GetFields(MemberData.flags)) {
					if(fields.Exists(m => m.Name == native.Name))
						continue;
					fields.Add(new RuntimeGraphExternalField(owner, native));
				}
			}
			foreach(var sibling in PartialGraphMembers.GetSiblingReflectionTypes(self)) {
				var siblingTarget = PartialGraphMembers.GetOwnerAsset(sibling);
				if(siblingTarget == null)
					continue;
				foreach(var variable in siblingTarget.GetVariables()) {
					if(fields.Exists(m => m.Name == variable.name))
						continue;
					fields.Add(new RuntimeGraphField(owner, new VariableRef(variable)));
				}
			}
		}

		public static void AppendExternalProperties(GraphAsset self, RuntimeType owner, List<PropertyInfo> properties) {
			var otherHalf = PartialGraphMembers.GetOtherHalfType(self);
			if(otherHalf != null) {
				foreach(var native in otherHalf.GetProperties(MemberData.flags)) {
					if(properties.Exists(m => m.Name == native.Name))
						continue;
					properties.Add(new RuntimeGraphExternalProperty(owner, native));
				}
			}
			foreach(var sibling in PartialGraphMembers.GetSiblingReflectionTypes(self)) {
				var siblingTarget = PartialGraphMembers.GetOwnerAsset(sibling);
				if(siblingTarget == null)
					continue;
				foreach(var property in siblingTarget.GetProperties()) {
					if(properties.Exists(m => m.Name == property.name))
						continue;
					properties.Add(new RuntimeGraphProperty(owner, new PropertyRef(property)));
				}
			}
		}

		public static void AppendExternalMethods(GraphAsset self, RuntimeType owner, List<MethodInfo> methods) {
			var otherHalf = PartialGraphMembers.GetOtherHalfType(self);
			if(otherHalf != null) {
				foreach(var native in otherHalf.GetMethods(MemberData.flags)) {
					if(native.IsSpecialName)
						continue;
					//Methods are matched on the full signature so overloads are kept.
					if(methods.Exists(m => m.Name == native.Name && SameParameters(m, native)))
						continue;
					methods.Add(new RuntimeGraphExternalMethod(owner, native));
				}
			}
			foreach(var sibling in PartialGraphMembers.GetSiblingReflectionTypes(self)) {
				var siblingTarget = PartialGraphMembers.GetOwnerAsset(sibling);
				if(siblingTarget == null)
					continue;
				foreach(var function in siblingTarget.GetFunctions()) {
					//Functions are matched on name and parameter types so overloads stay legal.
					if(methods.Exists(m => m.Name == function.name && SameParameters(m, function)))
						continue;
					methods.Add(new RuntimeGraphMethod(owner, new FunctionRef(function)));
				}
			}
		}

		public static bool SameParameters(MethodInfo method, MethodInfo native) {
			var parameters = method.GetParameters();
			var nativeParameters = native.GetParameters();
			if(parameters.Length != nativeParameters.Length)
				return false;
			for(int i = 0; i < parameters.Length; i++) {
				var type = parameters[i].ParameterType;
				if(type.IsByRef)
					type = type.GetElementType();
				if(type != nativeParameters[i].ParameterType)
					return false;
			}
			return true;
		}

		public static bool SameParameters(MethodInfo method, Function function) {
			var parameters = method.GetParameters();
			var functionParameters = function.Parameters;
			if(parameters.Length != functionParameters.Count)
				return false;
			for(int i = 0; i < parameters.Length; i++) {
				var type = parameters[i].ParameterType;
				if(type.IsByRef)
					type = type.GetElementType();
				if(type != functionParameters[i].Type)
					return false;
			}
			return true;
		}
	}
}
