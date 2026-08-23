using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MaxyGames.UNode {
    public class RuntimeGraphType : RuntimeType, IRuntimeType, ISummary, IIcon, IRuntimeMemberWithRef {
		/// <summary>
		/// The target of the graph
		/// </summary>
		public GraphAsset target { get; internal set; }

		public RuntimeGraphType(GraphAsset target) {
			this.target = target;
		}

		public override string Name {
			get {
				if(target != null) {
					return target.GetGraphName();
				}
				return string.Empty;
			}
		}

		public override string Namespace {
			get {
				if(target is INamespace graph) {
					return graph.Namespace;
				} else if(target is IScriptGraphType scriptGraphType) {
					if (scriptGraphType.ScriptTypeData.scriptGraph == null)
						throw new Exception("The ScriptGraph asset is null");
					return scriptGraphType.ScriptTypeData.scriptGraph.Namespace;
				}
				return base.Namespace;
			}
		}

		public override Type BaseType {
			get {
				if(target is ISingletonGraph) {
					//In case singleton, we simply return object type because we don't need to show inherited members
					return typeof(object);
				}
				if (target is IClassGraph classGraph) {
					return classGraph.InheritType;
				}
				return typeof(object);
			}
		}

        public override bool IsValid() {
			try {
				return target != null;
			} catch {
				return false;
			}
		}

		/// <summary>
		/// True if the type is a singleton type
		/// </summary>
		public bool IsSingleton => target is ISingletonGraph;

		#region Build
		public override void Update() {
			if(constructors != null) {
				BuildConstructors();
			}
			if(fields != null) {
				BuildFields();
			}
			if(properties != null) {
				BuildProperties();
			}
			if(methods != null) {
				BuildMethods();
			}
			if(events != null) {
				BuildEvents();
			}
		}

		ConstructorInfo[] constructors;
		private void BuildConstructors() {
			if(target is IClassDefinition definition && !definition.InheritType.IsCastableTo(typeof(UnityEngine.Object))) {
				if(constructors == null) {
					constructors = new[]
					{
						new RuntimeGraphConstructor(this, parameters => {
							return definition.GetModel().CreateInstance(definition.FullGraphName);
						})
					};
				}
				else {
					//constructors.Clear();
				}
				return;
			}
			constructors = Array.Empty<ConstructorInfo>();
		}

		protected List<FieldInfo> fields;
		Dictionary<int, RuntimeGraphField> m_runtimeFields = new Dictionary<int, RuntimeGraphField>();
		protected virtual void BuildFields() {
			var inheritMembers = BaseType != null ? BaseType.GetFields(MemberData.flags) : Array.Empty<FieldInfo>();
			if(fields == null) {
				List<FieldInfo> members = new List<FieldInfo>(inheritMembers);
				foreach(var m in target.GetVariables()) {
					var value = new RuntimeGraphField(this, new VariableRef(m));
					m_runtimeFields[m.id] = value;
					members.Add(value);
				}
				fields = members;
			}
			else {
				fields.Clear();
				List<FieldInfo> members = fields;
				members.AddRange(inheritMembers);
				foreach(var (_, field) in m_runtimeFields) {
					if(field.target.isValid) {
						members.Add(field);
					}
				}
				foreach(var m in target.GetVariables()) {
					if(!m_runtimeFields.ContainsKey(m.id)) {
						var value = new RuntimeGraphField(this, new VariableRef(m));
						m_runtimeFields[m.id] = value;
						members.Add(value);
					}
				}
				fields = members;
			}
		}

		protected List<PropertyInfo> properties;
		Dictionary<int, RuntimeGraphProperty> m_runtimeProperties = new Dictionary<int, RuntimeGraphProperty>();
		protected virtual void BuildProperties() {
			var inheritMembers = BaseType != null ? BaseType.GetProperties(MemberData.flags) : Array.Empty<PropertyInfo>();
			if(properties == null) {
				List<PropertyInfo> members = new List<PropertyInfo>(inheritMembers);
				foreach(var m in target.GetProperties()) {
					if(m.modifier.Override == false) {
						var value = new RuntimeGraphProperty(this, new PropertyRef(m));
						m_runtimeProperties[m.id] = value;
						members.Add(value);
					}
				}
				properties = members;
			}
			else {
				properties.Clear();
				List<PropertyInfo> members = properties;
				members.AddRange(inheritMembers);
				foreach(var (_, member) in m_runtimeProperties) {
					if(member.target.isValid) {
						members.Add(member);
					}
				}
				foreach(var m in target.GetProperties()) {
					if(m.modifier.Override == false && !m_runtimeProperties.ContainsKey(m.id)) {
						var value = new RuntimeGraphProperty(this, new PropertyRef(m));
						m_runtimeProperties[m.id] = value;
						members.Add(value);
					}
				}
				properties = members;
			}
		}

		protected List<MethodInfo> methods;
		Dictionary<int, RuntimeGraphMethod> m_runtimeMethods = new Dictionary<int, RuntimeGraphMethod>();
		protected virtual void BuildMethods() {
			var inheritMembers = BaseType != null ? BaseType.GetMethods(MemberData.flags) : Array.Empty<MethodInfo>();
			if(methods == null) {
				List<MethodInfo> members = new List<MethodInfo>(inheritMembers);
				foreach(var m in target.GetFunctions()) {
					if(m.modifier.Override == false) {
						var value = new RuntimeGraphMethod(this, new FunctionRef(m));
						m_runtimeMethods[m.id] = value;
						members.Add(value);
					}
				}
				methods = members;
			}
			else {
				methods.Clear();
				List<MethodInfo> members = methods;
				members.AddRange(inheritMembers);
				foreach(var (_, field) in m_runtimeMethods) {
					if(field.target.isValid) {
						members.Add(field);
					}
				}
				foreach(var m in target.GetFunctions()) {
					if(m.modifier.Override == false && !m_runtimeMethods.ContainsKey(m.id)) {
						var value = new RuntimeGraphMethod(this, new FunctionRef(m));
						m_runtimeMethods[m.id] = value;
						members.Add(value);
					}
				}
				methods = members;
			}
		}
		#endregion

		#region Partial
		/// <summary>
		/// True when this graph is marked `partial`, meaning it may combine with other halves.
		/// </summary>
		public bool IsPartialGraph {
			get {
				return target is IClassModifier modifier && modifier.GetModifier()?.Partial == true;
			}
		}

		/// <summary>
		/// The compiled CLR type of the hand-written half of this graph, or null when the
		/// graph has none. A partial graph is fully usable without an other half; when one
		/// exists, its members are merged into this type below.
		/// </summary>
		public Type OtherHalfType => PartialGraphMembers.GetOtherHalfType(target);

		protected List<EventInfo> events;
		protected virtual void BuildEvents() {
			if(events == null) {
				events = new List<EventInfo>();
			}
			else {
				events.Clear();
			}
		}

		public override EventInfo GetEvent(string name, BindingFlags bindingAttr) {
			if(events == null)
				BuildEvents();
			foreach(var evt in events) {
				if(evt.Name == name && IsEventValid(evt, bindingAttr))
					return evt;
			}
			return BaseType?.GetEvent(name, bindingAttr);
		}

		public override EventInfo[] GetEvents(BindingFlags bindingAttr) {
			if(events == null)
				BuildEvents();
			var result = new List<EventInfo>();
			if(BaseType != null) {
				result.AddRange(BaseType.GetEvents(bindingAttr));
			}
			foreach(var evt in events) {
				if(IsEventValid(evt, bindingAttr))
					result.Add(evt);
			}
			return result.ToArray();
		}

		private static bool IsEventValid(EventInfo evt, BindingFlags bindingAttr) {
			var flg = bindingAttr & (BindingFlags.Static | BindingFlags.Instance);
			if(flg == 0)
				return true;
			var accessor = evt.GetAddMethod(true) ?? evt.GetRemoveMethod(true);
			bool isStatic = accessor != null && accessor.IsStatic;
			if(isStatic)
				return (flg & BindingFlags.Static) != 0;
			return (flg & BindingFlags.Instance) != 0;
		}

		public override MemberInfo[] GetMembers(BindingFlags bindingAttr) {
			var constructors = GetConstructors(bindingAttr);
			var fields = GetFields(bindingAttr);
			var properties = GetProperties(bindingAttr);
			var methods = GetMethods(bindingAttr);
			var events = GetEvents(bindingAttr);
			var members = new MemberInfo[constructors.Length + fields.Length +
				properties.Length + methods.Length + events.Length];
			int idx = 0;
			for(int i = 0; i < constructors.Length; i++) {
				members[idx++] = constructors[i];
			}
			for(int i = 0; i < fields.Length; i++) {
				members[idx++] = fields[i];
			}
			for(int i = 0; i < properties.Length; i++) {
				members[idx++] = properties[i];
			}
			for(int i = 0; i < methods.Length; i++) {
				members[idx++] = methods[i];
			}
			for(int i = 0; i < events.Length; i++) {
				members[idx++] = events[i];
			}
			return members;
		}
		#endregion

		#region Overrides
		public override bool Equals(object obj) {
			if(obj == null) {
				return target == null || base.Equals(obj);
			}
			return base.Equals(obj);
		}

		public override int GetHashCode() {
			return target.GetHashCode();
		}

		public override object[] GetCustomAttributes(bool inherit) {
			if (target is IAttributeSystem) {
				return (target as IAttributeSystem).GetAttributes();
			}
			return new object[0];
		}

		public override object[] GetCustomAttributes(Type attributeType, bool inherit) {
			if (target is IAttributeSystem) {
				return (target as IAttributeSystem).GetAttributes(attributeType);
			}
			return new object[0];
		}

		public override ConstructorInfo[] GetConstructors(BindingFlags bindingAttr) {
			if(constructors == null) {
				BuildConstructors();
			}
			return ReflectionUtils.GetConstructorCandidates(constructors, bindingAttr, this).ToArray();
		}

		public override FieldInfo GetField(string name, BindingFlags bindingAttr) {
			if (fields == null)  {
				BuildFields();
			}
			for (int i = 0; i < fields.Count;i++) {
				if(fields[i].Name == name) {
					return fields[i];
				}
			}
			return null;
		}

		public override FieldInfo[] GetFields(BindingFlags bindingAttr) {
			if (fields == null) 
			{
				BuildFields();
			}
			return ReflectionUtils.GetFieldCandidates(fields, bindingAttr, this).ToArray();
		}

		public override Type[] GetInterfaces() {
			if (target is IInterfaceSystem interfaceSystem) {
				var ifaces = interfaceSystem.Interfaces;
				if(ifaces != null) {
					List<Type> types = new List<Type>();
					foreach(var iface in ifaces) {
						if(iface?.type != null) {
							types.Add(iface.type);
						}
					}
					if(target is IClassDefinition definition) {
						var model = definition.GetModel();
						if(model.ScriptInheritType is RuntimeType) {
							types.AddRange(model.ScriptInheritType.GetInterfaces());
						}
						else {
							types.Add(typeof(IRuntimeClass));
						}
					}
					return types.ToArray();
				}
			}
			if(target is IClassDefinition) {
				return new[] { typeof(IRuntimeClass) };
			}
			return Type.EmptyTypes;
		}

		public override bool IsAssignableFrom(Type c) {
			if(c == null) return false;
			if(this == c) {
				return true;
			}
			if(c.IsSubclassOf(this)) {
				return true;
			}
			//if(c == typeof(IRuntimeClass)) {
			//	return true;
			//}
			//if(target is IClassDefinition classDefinition) {
			//	var model = classDefinition.GetModel();
			//	if(c == model.ProxyScriptType) {
			//		return true;
			//	}
			//}
			return false;
		}

		public override bool IsSubclassOf(Type c) {
			if(target is IClassDefinition classDefinition) {
				var model = classDefinition.GetModel();
				if(c == model.ProxyScriptType) {
					return true;
				}
			}
			return base.IsSubclassOf(c);
		}

		public override bool IsInstanceOfType(object o) {
			if(o == null || o.Equals(target)) return false;
			if(o is IInstancedGraph || o is IRuntimeClass) {
				var type = ReflectionUtils.GetRuntimeType(o);
				if(type != null) {
					return type.IsCastableTo(this);
				}
			}
			var c = o.GetType();
			if(this == c) {
				return true;
			}
			if(c.IsSubclassOf(this)) {
				return true;
			}
			return false;
		}

		public override MethodInfo[] GetMethods(BindingFlags bindingAttr) {
			if (methods == null) {
				BuildMethods();
			}
			return ReflectionUtils.GetMethodCandidates(methods, bindingAttr, this).ToArray();
		}

		public override PropertyInfo[] GetProperties(BindingFlags bindingAttr) {
			if (properties == null) {
				BuildProperties();
			}
			return ReflectionUtils.GetPropertyCandidates(properties, bindingAttr, this).ToArray();
		}

		protected override TypeAttributes GetAttributeFlagsImpl() {
			if(IsSingleton) {
				return TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed;
			}
			if(BaseType == null) {
				return TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.ClassSemanticsMask | TypeAttributes.Abstract | TypeAttributes.Interface;
			}
			return TypeAttributes.Public | TypeAttributes.Class;
		}
		#endregion

		public string GetSummary() {
			return target.GraphData.comment;
		}

		public Type GetIcon() {
			if(target is IIcon icon) {
				return icon.GetIcon();
			}
			else if(target is ICustomIcon customIcon) {
				return TypeIcons.FromTexture(customIcon.GetIcon());
			}
			return null;
		}

		public BaseReference GetReference() {
			return new GraphRef(target);
		}

		public virtual Type GetNativeType() {
			if(target is IClassDefinition definition) {
				return definition.GetModel().ProxyScriptType;
			}
			else if(IsInterface) {
				return typeof(object);
			}
			return null;
		}
	}
}