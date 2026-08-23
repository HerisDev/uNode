using System;
using System.Collections.Generic;
using System.Reflection;

namespace MaxyGames.UNode {
	/// <summary>
	/// Marks a runtime type that presents the combined view of a `partial` graph class:
	/// the owning half's authored members plus every sibling half and the compiled
	/// hand-written C# class when one exists.
	/// </summary>
	public interface IPartialGraphType {
		/// <summary>
		/// The primary half of the class. It re-points to a surviving half whenever the
		/// current one is deleted.
		/// </summary>
		GraphAsset ownerAsset { get; }

		/// <summary>
		/// Every known half of the class; deleted ones are pruned by the registry sync.
		/// </summary>
		IList<GraphAsset> ownerAssets { get; }

		/// <summary>
		/// The full class name, captured at creation for fast identification and as the
		/// serialization label of the reference.
		/// </summary>
		string fullTypeName { get; }
	}

	/// <summary>
	/// The one runtime type of a `partial` graph class.
	/// One instance per full name is kept by the editor registry, so every half's
	/// <see cref="IReflectionType.ReflectionType"/> resolves to this same object and all
	/// consumers see a single combined type.
	/// It implements <see cref="INativeType"/> because a partial class always has a real CLR
	/// presence: the merged generated class once produced, otherwise the compiled
	/// hand-written half while only it exists.
	/// The member builds below own the combining: base builds produce the owning half's
	/// authored members only, then every other half is merged on top through
	/// <see cref="PartialGraphMerge"/>.
	/// Its reference (<see cref="PartialTypeRef"/>) persists all halves plus the type name,
	/// so serialized usages survive deleting any - or all - of the graph assets.
	/// </summary>
	public class RuntimePartialGraphType : RuntimeGraphType, IPartialGraphType, INativeType {
		private readonly List<GraphAsset> m_ownerAssets = new List<GraphAsset>();
		private readonly string m_fullTypeName;

		public RuntimePartialGraphType(GraphAsset target) : base(target) {
			m_fullTypeName = target != null ? target.GetFullGraphName() : string.Empty;
			if(target != null) {
				m_ownerAssets.Add(target);
			}
		}

		GraphAsset IPartialGraphType.ownerAsset => target;

		IList<GraphAsset> IPartialGraphType.ownerAssets => m_ownerAssets;

		string IPartialGraphType.fullTypeName => m_fullTypeName;

		/// <summary>
		/// Registry sync: prune deleted halves, register newly discovered ones and re-point
		/// the primary target to a survivor whenever it died.
		/// </summary>
		internal void SyncOwnerAssets(IEnumerable<GraphAsset> halves) {
			m_ownerAssets.RemoveAll(h => h == null);
			foreach(var half in halves) {
				if(half == null)
					continue;
				if(!m_ownerAssets.Contains(half)) {
					m_ownerAssets.Add(half);
				}
			}
			if(target == null && m_ownerAssets.Count > 0) {
				target = m_ownerAssets[0];
			}
		}

		public override bool IsValid() {
			try {
				for(int i = 0; i < m_ownerAssets.Count; i++) {
					if(m_ownerAssets[i] != null)
						return true;
				}
				//Even with every asset gone, the generated merged class keeps the type usable.
				return GetNativeType() != null;
			}
			catch {
				return false;
			}
		}

		public override BaseReference GetReference() {
			return new PartialTypeRef(this);
		}

		protected override void BuildFields() {
			base.BuildFields();
			PartialGraphMerge.AppendExternalFields(target, this, fields);
		}

		protected override void BuildProperties() {
			base.BuildProperties();
			PartialGraphMerge.AppendExternalProperties(target, this, properties);
		}

		protected override void BuildMethods() {
			base.BuildMethods();
			PartialGraphMerge.AppendExternalMethods(target, this, methods);
		}

		protected override void BuildEvents() {
			base.BuildEvents();
			//Only a human-authored half can declare events; everything the graph generated
			//mirrors authored members, which carry no event infos of their own.
			if(PartialGraphMembers.Get(target).Count == 0)
				return;
			var otherHalf = PartialGraphMembers.GetCompiledType(target);
			if(otherHalf != null) {
				foreach(var nativeEvent in otherHalf.GetEvents(MemberData.flags)) {
					events.Add(new RuntimeGraphExternalEvent(this, nativeEvent));
				}
			}
		}

		public override Type GetNativeType() {
			var otherHalf = PartialGraphMembers.GetCompiledType(target);
			if(otherHalf != null)
				return otherHalf;
			return base.GetNativeType();
		}
	}
}
