﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Localization;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.SystemModule;
using DevExpress.ExpressApp.Utils;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.Validation;
using Xpand.ExpressApp.Editors;
using Xpand.ExpressApp.ExcelImporter.BusinessObjects;
using Xpand.ExpressApp.ExcelImporter.Services;
using Xpand.Persistent.Base.General;
using Xpand.Persistent.Base.Validation;
using Xpand.XAF.Modules.MasterDetail;

namespace Xpand.ExpressApp.ExcelImporter.Controllers{
    public class ExcelImportDetailViewController : ObjectViewController<DetailView,ExcelImport>{
        protected Subject<Unit> Terminator=new Subject<Unit>();


        private const string ExcelMapActionName = "ExcelMap";
        private const string ImportExcelActionName = "ImportExcel";
        
        public SingleChoiceAction MapAction{ get; }

        public ExcelImportDetailViewController(){
            MapAction = new SingleChoiceAction(this, ExcelMapActionName, "ExcelImport") {
                 ItemType = SingleChoiceActionItemType.ItemIsOperation
            };
            MapAction.Items.Add(new ChoiceActionItem("Map", "Map"));
            MapAction.Items.Add(new ChoiceActionItem("Reset", "Reset"));
            MapAction.Execute+=ExcelMappingActionOnExecute;
            ImportAction = new SimpleAction(this,ImportExcelActionName,"ExcelImport"){Caption = "Import"};
            ImportAction.Execute+=ImportActionOnExecute;
            ImportAction.Executing+=ImportActionOnExecuting;
        }

        private void ExcelMappingActionOnExecute(object sender, SingleChoiceActionExecuteEventArgs e) {
            if ((string) e.SelectedChoiceActionItem.Data == "Reset") {
                ObjectSpace.Delete(ExcelImport.ExcelColumnMaps);
                ObjectSpace.CommitChanges();
                MapAction.DoExecute(MapAction.FindItemByIdPath("Map"));
            }
            else {
                if (!ExcelImport.ExcelColumnMaps.Any())
                    Map();
                ObjectSpace.CommitChanges();
                var parameters = e.ShowViewParameters;
                MasterDetailService.MasterDetailDashboardViewItems
                    .FirstAsync()
                    .Select(tuple => {
                        var listView = ((ListView) tuple.listViewItem.InnerView);
                        var criteriaOperator = listView.ObjectSpace.GetCriteriaOperator<ExcelColumnMap>(map =>map.ExcelImport.Oid == ExcelImport.Oid);
                        listView.CollectionSource.Criteria[GetType().Name] =criteriaOperator;
                        return Unit.Default;
                    })
                    .Subscribe();


                var dialogController = new DialogController();
                parameters.Controllers.Add(dialogController);
                parameters.CreatedView=Application.CreateDashboardView(Application.CreateObjectSpace(), "ExcelColumnMapMasterDetail", true);
                parameters.TargetWindow=TargetWindow.NewModalWindow;
                ShowMapConfigView(parameters);
                dialogController.AcceptAction.Executed+=AcceptActionOnExecuted;
            }
            
        }

        private void AcceptActionOnExecuted(object sender, ActionBaseEventArgs e) {
            ((IObjectSpaceLink) ExcelImport).ObjectSpace.ReloadObject(ExcelImport);
            ExcelImport.ValidateForImport();
        }

        protected virtual void ShowMapConfigView(ShowViewParameters parameters) {
            
        }

        private void Map() {
            ValidateFile();
            var excelImport = ExcelImport;
            excelImport.Map();   
        }

        protected override void OnDeactivated(){
            base.OnDeactivated();
            Terminator.OnNext(Unit.Default);
        }


        private void ImportActionOnExecuting(object sender, CancelEventArgs cancelEventArgs){
            ValidateFile();
            ExcelImport.ValidateForImport();
        }

        protected ExcelImport ExcelImport => ((ExcelImport) View.CurrentObject);
        protected virtual void ValidateFile(){
            if (ExcelImport.File.Content == null){
                var result = Validator.RuleSet.NewRuleSetValidationMessageResult(ObjectSpace, "Invalid file", "Save",
                    View.CurrentObject, View.ObjectTypeInfo.Type, new List<string>{nameof(ExcelImport.File)});
                throw new ValidationException("", result);
            }
            
        }

        private void ImportActionOnExecute(object sender, SimpleActionExecuteEventArgs e){
            var progressBarViewItem = View.GetItems<ProgressViewItem>().First();
            progressBarViewItem.Start();
            var progressObserver = GetProgressObserver(ExcelImport,progressBarViewItem);
            ObjectSpace.SetModified(View.CurrentObject);
            var importParameters = ExcelImport.GetImportParameters();
            Observable.Start(async () => {
                var space = Application.CreateObjectSpace(ExcelImport.GetType());
                var excelImport = space.GetObjectByKey<ExcelImport>(ExcelImport.Oid);
                excelImport.Import(ExcelImport.File.Content, progressObserver,importParameters);
                await ObjectSpace.WhenCommiting().FirstAsync();
                return space;
            })
            .SelectMany(task => task)
            .Catch<IObjectSpace,Exception>(exception => Synchronize(0).Select(i => Observable.Throw<IObjectSpace>(exception)).Concat())
            .Subscribe(space => {
                    space.CommitChanges();
                    space.Dispose();
                });
        }

        protected virtual IObserver<ImportProgress> GetProgressObserver(ExcelImport excelImport,ProgressViewItem progressBarViewItem) {
            var progress = CreateProgressObserver();
            BeginImport.OnNext((excelImport, progress));
            var resultMessage = (CaptionHelper.GetLocalizedText(ExcelImporterLocalizationUpdater.ExcelImport,
                    ExcelImporterLocalizationUpdater.ImportSucceded),CaptionHelper.GetLocalizedText(ExcelImporterLocalizationUpdater.ExcelImport,
                ExcelImporterLocalizationUpdater.ImportFailed));
            
            var dataRowProgress = progress.OfType<ImportDataRowProgress>().Where(excelImport);
            
            var progressEnd = ProgressEnd(excelImport, progressBarViewItem, progress, resultMessage,Application.Model.BOModel.ToDictionary(c => c.TypeInfo.FullName,c => c.Caption));
            progress.OfType<ImportProgressStart>().Where(excelImport)
                .Do(OnStart).TakeUntil(progressEnd).Subscribe();
            
            Observable
                .Interval(TimeSpan.FromMilliseconds(progressBarViewItem.PollingInterval))
                .WithLatestFrom(dataRowProgress, (l, importProgress) => ( importProgress.Percentage))
                .Select(Synchronize).Concat( )
                .Finally(() => OnSetPosition(progressBarViewItem, 0))
                .TakeUntil(progressEnd)
                .Do(percentage => OnSetPosition(progressBarViewItem, percentage))
                .Subscribe();
            return progress;
        }

        protected virtual void OnStart(ImportProgressStart importProgressStart) {
            
        }

        public Subject<(ExcelImport excelImport, ISubject<ImportProgress> progress)> BeginImport { get; } = new Subject<(ExcelImport excelImport, ISubject<ImportProgress> progress)>();

        protected virtual ISubject<ImportProgress> CreateProgressObserver(){
            return Subject.Synchronize(new Subject<ImportProgress>());
        }

        protected  virtual IObservable<T> Synchronize<T>(T i=default) {
            return Observable.Return(i);
        }

        protected virtual IObservable<Unit> ProgressEnd(ExcelImport excelImport, ProgressViewItem progressBarViewItem,
            ISubject<ImportProgress> progress, (string successMsg, string failedMsg) resultMessage,
            Dictionary<string, string> boCaptions) {
            Terminator.OnNext(Unit.Default);
            var boModel = Application.Model.BOModel;
            return progress.OfType<ImportProgressComplete>().Where(excelImport).FirstAsync()
                .Select(Synchronize).Concat()
                .Do(_ => {
                    ExcelImport.FailedResultList.FailedResults = _.FailedResults;
                    View.FindItem($"{nameof(BusinessObjects.ExcelImport.FailedResultList)}.{nameof(FailedResultList.FailedResults)}").Refresh();
                    progressBarViewItem.SetFinishOptions(GetFinishOptions(_, resultMessage,boModel));
                })
                .ToUnit()
                .Merge(Terminator);
        }

        protected virtual void OnSetPosition(ProgressViewItem progressBarViewItem, int percentage){
            progressBarViewItem.SetPosition ( percentage);
        }

        protected virtual MessageOptions GetFinishOptions(ImportProgressComplete progressComplete,
            (string successMsg, string failedMsg) resultMessage, IModelBOModel boModel ){
            var failedResults = progressComplete.FailedResults;
            string message;
            var informationType=InformationType.Success;
            if (failedResults.Any()){
                informationType = InformationType.Error;
                message = string.Format(resultMessage.failedMsg, failedResults.GroupBy(r => r.Index).Count(), progressComplete.FailedResults.Count);
            }
            else{
                message =string.Format(resultMessage.successMsg, progressComplete.TotalRecordsCount);
            }
            var messageOptions = NewMessageOptions( );
            messageOptions.Type=informationType;
            messageOptions.Message=message;
            if (progressComplete is ImportProgressException progressException) {
                messageOptions.Type=InformationType.Error;
                messageOptions.Message = progressException.Exception.Message;
                if (progressException.Exception is MemberNotFoundException memberNotFoundException) {
                    var exceptionMessage = SystemExceptionLocalizer.GetExceptionMessage(ExceptionId.CannotFindThePropertyWithinTheClass, memberNotFoundException.MemberName, boModel[memberNotFoundException.TypeName]);
                    messageOptions.Message=exceptionMessage;
                }
                if (progressException.Exception is KeyMemberNotMappedException keyMemberNotMappedException) {
                    var modelClass = boModel.GetClass(keyMemberNotMappedException.Type);
                    messageOptions.Message=$"Default member for {modelClass.Caption} is missing";
                }
            }
            return messageOptions;
        }

        protected virtual MessageOptions NewMessageOptions(){
            var messageOptions = new MessageOptions{
                Duration = Int32.MaxValue,
                Win = {Type = WinMessageType.Alert},
                Web = {Position = InformationPosition.Top, CanCloseOnOutsideClick = false, CanCloseOnClick = true}
            };
            return messageOptions;
        }

        public SimpleAction ImportAction{ get;  }
    }
}