﻿namespace MaxyGames.UNode.Transition {
	[TransitionMenu("OnTriggerExit2D", "OnTriggerExit2D")]
	public class OnTriggerExit2D : TransitionEvent {
		[Filter(typeof(UnityEngine.Collider2D), SetMember = true)]
		public MemberData storeCollider = new MemberData();

		public override void OnEnter(Flow flow) {
			UEvent.Register<UnityEngine.Collider2D>(UEventID.OnTriggerExit2D, flow.target as UnityEngine.Component, (value) => Execute(flow, value));
		}

		public override void OnExit(Flow flow) {
			UEvent.Unregister<UnityEngine.Collider2D>(UEventID.OnTriggerExit2D, flow.target as UnityEngine.Component, (value) => Execute(flow, value));
		}

		void Execute(Flow flow, UnityEngine.Collider2D collider) {
			if(storeCollider.isAssigned) {
				storeCollider.Set(flow, collider);
			}
			Finish(flow);
		}

		public override string GenerateOnEnterCode() {
			if(!CG.HasInitialized(this)) {
				CG.SetInitialized(this);
				var mData = CG.generatorData.GetMethodData("OnTriggerExit2D");
				if(mData == null) {
					mData = CG.generatorData.AddMethod(
						"OnTriggerExit2D",
						typeof(void),
						typeof(UnityEngine.Collider2D));
				}
				string set = null;
				if(storeCollider.isAssigned) {
					set = CG.Set(CG.Value((object)storeCollider), mData.parameters[0].name).AddLineInEnd();
				}
				mData.AddCode(
					CG.Condition(
						"if",
						CG.CompareNodeState(node.enter, null),
						set + CG.FlowTransitionFinish(this)
					),
					this
				);
			}
			return null;
		}
	}
}
