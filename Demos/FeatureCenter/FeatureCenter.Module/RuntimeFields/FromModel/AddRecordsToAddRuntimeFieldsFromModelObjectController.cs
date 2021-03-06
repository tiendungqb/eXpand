﻿using DevExpress.ExpressApp;

namespace FeatureCenter.Module.RuntimeFields.FromModel {
    public class AddRecordsToAddRuntimeFieldsFromModelObjectController : ViewController<ListView> {
        public AddRecordsToAddRuntimeFieldsFromModelObjectController() {
            TargetObjectType = typeof(Customer);
            TargetViewId = "RuntimeFieldsFromModel_ListView";
        }
        protected override void OnActivated() {
            base.OnActivated();
            int i = 0;
            foreach (var o in ObjectSpace.GetObjects(View.ObjectTypeInfo.Type)) {
                View.ObjectTypeInfo.FindMember("RuntimeMember").SetValue(o, "I am runtime member " + i);
                i++;
            }

        }
    }
}