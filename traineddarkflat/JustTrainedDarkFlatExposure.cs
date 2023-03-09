#region "copyright"

/*
    Copyright © 2016 - 2022 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.Trigger;
using NINA.Equipment.Interfaces.Mediator;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NINA.Equipment.Model;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.Core.Locale;
using NINA.Profile;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.Sequencer.Utility;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.FlatDevice;

namespace Naixx.NINA.Justtraineddarkflatexposure.JusttraineddarkflatexposureTestCategory {

    [ExportMetadata("Name", "TrainedDarkFlatExposure without flat cover")]
    [ExportMetadata("Description", "You can use this item only when you need to run trained dark flat exposure without opening/closing flat panel")]
    [ExportMetadata("Icon", "BrainBulbSVG")]
    [ExportMetadata("Category", "Lbl_SequenceCategory_FlatDevice")]
    [Export(typeof(ISequenceItem))]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class JustTrainedDarkFlatExposure : SequentialContainer, IImmutableContainer {

        [OnDeserializing]
        public void OnDeserializing(StreamingContext context) {
            this.Items.Clear();
            this.Conditions.Clear();
            this.Triggers.Clear();
        }

        private IProfileService profileService;

        [ImportingConstructor]
        public JustTrainedDarkFlatExposure(IProfileService profileService, ICameraMediator cameraMediator, IImagingMediator imagingMediator, IImageSaveMediator imageSaveMediator, IImageHistoryVM imageHistoryVM, IFilterWheelMediator filterWheelMediator, IFlatDeviceMediator flatDeviceMediator) :
            this(
                null,
                profileService,
                new ToggleLight(flatDeviceMediator) { OnOff = false },
                new SwitchFilter(profileService, filterWheelMediator),
                new SetBrightness(flatDeviceMediator),
                new TakeExposure(profileService, cameraMediator, imagingMediator, imageSaveMediator, imageHistoryVM) { ImageType = CaptureSequence.ImageTypes.DARKFLAT },
                new LoopCondition() { Iterations = 1 }

            ) {
        }

        private JustTrainedDarkFlatExposure(
            JustTrainedDarkFlatExposure cloneMe,
            IProfileService profileService,
         
            ToggleLight toggleLightOff,
            SwitchFilter switchFilter,
            SetBrightness setBrightness,
            TakeExposure takeExposure,
            LoopCondition loopCondition
            ) {
            this.profileService = profileService;

            this.Add(toggleLightOff);
            this.Add(switchFilter);
            this.Add(setBrightness);

            var container = new SequentialContainer();
            container.Add(loopCondition);
            container.Add(takeExposure);
            this.Add(container);

            IsExpanded = false;

            if (cloneMe != null) {
                CopyMetaData(cloneMe);
            }
        }

        private InstructionErrorBehavior errorBehavior = InstructionErrorBehavior.ContinueOnError;

        [JsonProperty]
        public override InstructionErrorBehavior ErrorBehavior {
            get => errorBehavior;
            set {
                errorBehavior = value;
                foreach (var item in Items) {
                    item.ErrorBehavior = errorBehavior;
                }
                RaisePropertyChanged();
            }
        }

        private int attempts = 1;

        [JsonProperty]
        public override int Attempts {
            get => attempts;
            set {
                if (value > 0) {
                    attempts = value;
                    foreach (var item in Items) {
                        item.Attempts = attempts;
                    }
                    RaisePropertyChanged();
                }
            }
        }

        public ToggleLight GetToggleLightOffItem() {
            return (Items[0] as ToggleLight);
        }

        public SwitchFilter GetSwitchFilterItem() {
            return (Items[1] as SwitchFilter);
        }

        public SetBrightness GetSetBrightnessItem() {
            return (Items[2] as SetBrightness);
        }

        public SequentialContainer GetImagingContainer() {
            return (Items[3] as SequentialContainer);
        }

        public TakeExposure GetExposureItem() {
            return ((Items[3] as SequentialContainer).Items[0] as TakeExposure);
        }

        public LoopCondition GetIterations() {
            return ((Items[3] as IConditionable).Conditions[0] as LoopCondition);
        }

        public override object Clone() {
            var clone = new JustTrainedDarkFlatExposure(
                this,
                profileService,
                (ToggleLight)this.GetToggleLightOffItem().Clone(),
                (SwitchFilter)this.GetSwitchFilterItem().Clone(),
                (SetBrightness)this.GetSetBrightnessItem().Clone(),
                (TakeExposure)this.GetExposureItem().Clone(),
                (LoopCondition)this.GetIterations().Clone()
            ) ;
            return clone;
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var loop = GetIterations();
            if (loop.CompletedIterations >= loop.Iterations) {
                Logger.Warning($"The Trained Dark Flat Exposure progress is already complete ({loop.CompletedIterations}/{loop.Iterations}). The instruction will be skipped");
                throw new SequenceItemSkippedException($"The Trained Dark Flat Exposure progress is already complete ({loop.CompletedIterations}/{loop.Iterations}). The instruction will be skipped");
            }

            /* Lookup trained values and set brightness and exposure time accordingly */
            var filter = GetSwitchFilterItem()?.Filter;
            var takeExposure = GetExposureItem();
            var binning = takeExposure.Binning;
            var gain = takeExposure.Gain == -1 ? profileService.ActiveProfile.CameraSettings.Gain ?? -1 : takeExposure.Gain;
            var info = profileService.ActiveProfile.FlatDeviceSettings.GetBrightnessInfo(new FlatDeviceFilterSettingsKey(filter?.Position, binning, gain));

            GetSetBrightnessItem().Brightness = 0;
            takeExposure.ExposureTime = info.Time;

        

            var toggleLightOff = GetToggleLightOffItem();
            if (!toggleLightOff.Validate()) {
                toggleLightOff.Skip();
                GetSetBrightnessItem().Skip();
            }

            return base.Execute(progress, token);
        }

        public override bool Validate() {
            var switchFilter = GetSwitchFilterItem();
            var takeExposure = GetExposureItem();
            var setBrightness = GetSetBrightnessItem();

            var valid = takeExposure.Validate() && switchFilter.Validate() && setBrightness.Validate();

            var issues = new List<string>();

            if (valid) {
                var filter = switchFilter?.Filter;
                var binning = takeExposure.Binning;
                var gain = takeExposure.Gain == -1 ? profileService.ActiveProfile.CameraSettings.Gain ?? -1 : takeExposure.Gain;
                if (profileService.ActiveProfile.FlatDeviceSettings.GetBrightnessInfo(new FlatDeviceFilterSettingsKey(filter?.Position, binning, gain)) == null) {
                    issues.Add(string.Format(Loc.Instance["Lbl_SequenceItem_Validation_FlatDeviceTrainedExposureNotFound"], filter?.Name, gain, binning?.Name));
                    valid = false;
                }
            }

            Issues = issues.Concat(takeExposure.Issues).Concat(switchFilter.Issues).Concat(setBrightness.Issues).Distinct().ToList();
            RaisePropertyChanged(nameof(Issues));

            return valid;
        }

        /// <summary>
        /// When an inner instruction interrupts this set, it should reroute the interrupt to the real parent set
        /// </summary>
        /// <returns></returns>
        public override Task Interrupt() {
            return this.Parent?.Interrupt();
        }
    }
}