//
// LICENSE:
// This work is licensed under the
//     Creative Commons Attribution-NonCommercial-ShareAlike 3.0 Unported License.
// also known as CC-BY-NC-SA.  To view a copy of this license, visit
//      http://creativecommons.org/licenses/by-nc-sa/3.0/
// or send a letter to
//      Creative Commons // 171 Second Street, Suite 300 // San Francisco, California, 94105, USA.
//
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Windows.Media;
using Buddy.Coroutines;
using Clio.Utilities;
using Clio.XmlEngine;
using ff14bot.Behavior;
using ff14bot.BotBases;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.RemoteAgents;
using ff14bot.RemoteWindows;
using NeoGaia.ConnectionHandler;
using TreeSharp;
using Action = TreeSharp.Action;
namespace ff14bot.NeoProfiles.Tags
{
    // Visual Studio's "Designer" requires the form to be the first class in the file...
    // <sigh> So much for alphabetical class listings.
    internal class UserDialogForm : Form
    {
        public UserDialogForm(AsyncCompletionToken completionToken,
                              string toonName,
                              string dialogTitle,
                              string dialogMessage,
                              string expiryActionName,
                              int expiryRemainingInSeconds,
                              bool isBotStopAllowed,
                              bool isBotContinueAllowed,
                              bool isStopOnContinue,
                              SystemSound soundCue,
                              int soundCuePeriodInSeconds)
        {
            _completionToken = completionToken;
            _completionToken.PopdownResponse = PopdownReason.UNKNOWN;
            _expiryActionHandler = ExpiryActionHandler.GetEnumItemByName(expiryActionName);
            _expiryRemainingInSeconds = expiryRemainingInSeconds;
            _isBotStopAllowed = isBotStopAllowed;
            _isBotContinueAllowed = isBotContinueAllowed;
            _isStopOnContinue = isStopOnContinue;
            _soundCue = soundCue;
            _soundCuePeriodInSeconds = soundCuePeriodInSeconds;


            // Dialog creation
            InitializeComponent();

            this.ControlBox = false;    // disable close box for this dialog
            this.MinimizeBox = false;    // disable minimize box for this dialog
            this.MaximizeBox = false;    // disable maximize box for this dialog
            this.FormBorderStyle = FormBorderStyle.FixedDialog;

            this.FormClosing += new FormClosingEventHandler(dialogForm_FormClosing);

            _heartbeatPulseTimer.Stop();

            TreeRoot.OnStop += TreeRoot_OnStop;

            // Dialog identity
            this.Text = String.Format("['{0}' UserDialog] {1}", toonName, dialogTitle);
            _textBoxMessage.Text = dialogMessage.Replace("\\n", Environment.NewLine).Replace("\\t", "\t");
            _textBoxMessage.SelectionStart = _textBoxMessage.SelectionLength;
            _checkBoxAutoDefend.Checked = _completionToken.IsAutoDefend;
			_checkBoxDisableMovement.Checked = _completionToken.DisableMovement;
            _buttonStopBot.Enabled = isBotStopAllowed;
            _buttonContinueProfile.Enabled = isBotContinueAllowed;
            _labelStatus.Text = "";

            // If only 'stop' allowed, convert the normally 'profile continue' button to 'stop'
            if (_isStopOnContinue)
            {
                _buttonStopBot.Visible = false;
                _buttonContinueProfile.Text = "Stop Bot";
            }


            // Setup the Expiry countdown, if enabled--
            // Our pulse timer goes off every second to notify the user how much time
            // remains before the expiry action will be executed.
            if (_expiryRemainingInSeconds > 0)
            {
                _expiryActionHandler.Initialize(this);
                _labelStatus.Text = UtilBuildTimeRemainingStatusText(_expiryActionHandler.ActionAsString(), _expiryRemainingInSeconds);
            }


            // Setup the audible warnings --
            // Note: *Never* try to set the System.Windows.Forms.Timer.Interval to zero.
            // Doing so will trigger a Windoze bug that prevents the dialog from
            // opening.
            switch (_soundCuePeriodInSeconds)
            {
                case 0:
                    // Play no sound--nothing to do
                    _soundPeriodRemaining = 0;
                    _checkBoxSuppressAudio.Enabled = false;
                    break;

                case 1:
                    // Play sound once for dialog open
                    _soundPeriodRemaining = 0;
                    soundCue.Play();
                    _checkBoxSuppressAudio.Enabled = false;
                    break;

                default:
                    // Play sound now for dialog open, and
                    // arrange to play the sound at the specified intervals
                    _soundPeriodRemaining = _soundCuePeriodInSeconds;
                    soundCue.Play();
                    _checkBoxSuppressAudio.Enabled = true;
                    break;
            }
            
            

            // Our heartbeat pulse handler looks for things like timer expiry, and dialog close requests.
            // Polling techniques like this offend us; however, our choices are limited, since
            // .NET requires the actual processing of a request to occur on the same thread
            // that created the dialog and its widgets.  Yet, .NET provides us with no clean mechanisms
            // for external event notifications other than through timers.
            _heartbeatPulseTimer.Interval = 1000;    // one second
            _heartbeatPulseTimer.Enabled = true;
        }


        #region imports

        // To support flashing.
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        //Flash both the window caption and taskbar button.
        //This is equivalent to setting the FLASHW_CAPTION | FLASHW_TRAY flags. 
        public const UInt32 FLASHW_ALL = 3;

        // Flash continuously until the window comes to the foreground. 
        public const UInt32 FLASHW_TIMERNOFG = 12;

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public UInt32 cbSize;
            public IntPtr hwnd;
            public UInt32 dwFlags;
            public UInt32 uCount;
            public UInt32 dwTimeout;
        }

        // Do the flashing - this does not involve a raincoat.
        public static bool FlashWindowEx(Form form)
        {
            IntPtr hWnd = form.Handle;
            FLASHWINFO fInfo = new FLASHWINFO();

            fInfo.cbSize = Convert.ToUInt32(Marshal.SizeOf(fInfo));
            fInfo.hwnd = hWnd;
            fInfo.dwFlags = FLASHW_ALL;
            fInfo.uCount = 5;
            fInfo.dwTimeout = 0;

            return FlashWindowEx(ref fInfo);
        }

        #endregion

        private IContainer components;
        private CheckBox _checkBoxDisableMovement;
        private bool closeRequested;
        private void TreeRoot_OnStop(AClasses.BotBase bot)
        {
            closeRequested = true;
        }


        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer _components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && _components != null)
            {
                _components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this._textBoxMessage = new System.Windows.Forms.TextBox();
            this._buttonContinueProfile = new System.Windows.Forms.Button();
            this._buttonStopBot = new System.Windows.Forms.Button();
            this._checkBoxSuppressAudio = new System.Windows.Forms.CheckBox();
            this._checkBoxAutoDefend = new System.Windows.Forms.CheckBox();
            this._labelStatus = new System.Windows.Forms.Label();
            this._heartbeatPulseTimer = new System.Windows.Forms.Timer(this.components);
            this._checkBoxDisableMovement = new System.Windows.Forms.CheckBox();
            this.SuspendLayout();
            // 
            // _textBoxMessage
            // 
            this._textBoxMessage.BackColor = System.Drawing.SystemColors.ButtonFace;
            this._textBoxMessage.Location = new System.Drawing.Point(12, 12);
            this._textBoxMessage.Multiline = true;
            this._textBoxMessage.Name = "_textBoxMessage";
            this._textBoxMessage.ReadOnly = true;
            this._textBoxMessage.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this._textBoxMessage.Size = new System.Drawing.Size(404, 134);
            this._textBoxMessage.TabIndex = 0;
            // 
            // _buttonContinueProfile
            // 
            this._buttonContinueProfile.Location = new System.Drawing.Point(319, 202);
            this._buttonContinueProfile.Name = "_buttonContinueProfile";
            this._buttonContinueProfile.Size = new System.Drawing.Size(101, 23);
            this._buttonContinueProfile.TabIndex = 1;
            this._buttonContinueProfile.Text = "Continue Profile";
            this._buttonContinueProfile.UseVisualStyleBackColor = true;
            this._buttonContinueProfile.Click += new System.EventHandler(this.buttonContinueProfile_Click);
            // 
            // _buttonStopBot
            // 
            this._buttonStopBot.Enabled = false;
            this._buttonStopBot.Location = new System.Drawing.Point(232, 201);
            this._buttonStopBot.Name = "_buttonStopBot";
            this._buttonStopBot.Size = new System.Drawing.Size(81, 23);
            this._buttonStopBot.TabIndex = 2;
            this._buttonStopBot.Text = "Stop Bot";
            this._buttonStopBot.UseVisualStyleBackColor = true;
            this._buttonStopBot.Click += new System.EventHandler(this.buttonStopBot_Click);
            // 
            // _checkBoxSuppressAudio
            // 
            this._checkBoxSuppressAudio.AutoSize = true;
            this._checkBoxSuppressAudio.Location = new System.Drawing.Point(12, 205);
            this._checkBoxSuppressAudio.Name = "_checkBoxSuppressAudio";
            this._checkBoxSuppressAudio.Size = new System.Drawing.Size(197, 17);
            this._checkBoxSuppressAudio.TabIndex = 3;
            this._checkBoxSuppressAudio.Text = "Suppress Periodic Audible Warnings";
            this._checkBoxSuppressAudio.UseVisualStyleBackColor = true;
            // 
            // _checkBoxAutoDefend
            // 
            this._checkBoxAutoDefend.AutoSize = true;
            this._checkBoxAutoDefend.Location = new System.Drawing.Point(12, 159);
            this._checkBoxAutoDefend.Name = "_checkBoxAutoDefend";
            this._checkBoxAutoDefend.Size = new System.Drawing.Size(142, 17);
            this._checkBoxAutoDefend.TabIndex = 4;
            this._checkBoxAutoDefend.Text = "Auto Defend, if attacked";
            this._checkBoxAutoDefend.UseVisualStyleBackColor = true;
            this._checkBoxAutoDefend.CheckedChanged += new System.EventHandler(this.checkBoxAutoDefend_CheckedChanged);
            // 
            // _labelStatus
            // 
            this._labelStatus.AutoSize = true;
            this._labelStatus.Location = new System.Drawing.Point(229, 179);
            this._labelStatus.MinimumSize = new System.Drawing.Size(180, 0);
            this._labelStatus.Name = "_labelStatus";
            this._labelStatus.Size = new System.Drawing.Size(180, 13);
            this._labelStatus.TabIndex = 5;
            this._labelStatus.TextAlign = System.Drawing.ContentAlignment.BottomRight;
            // 
            // _heartbeatPulseTimer
            // 
            this._heartbeatPulseTimer.Interval = 1000;
            this._heartbeatPulseTimer.Tick += new System.EventHandler(this.heartbeatPulseTimer_Tick);
            // 
            // _checkBoxDisableMovement
            // 
            this._checkBoxDisableMovement.AutoSize = true;
            this._checkBoxDisableMovement.Location = new System.Drawing.Point(12, 182);
            this._checkBoxDisableMovement.Name = "_checkBoxDisableMovement";
            this._checkBoxDisableMovement.Size = new System.Drawing.Size(114, 17);
            this._checkBoxDisableMovement.TabIndex = 6;
            this._checkBoxDisableMovement.Text = "Disable Movement";
            this._checkBoxDisableMovement.UseVisualStyleBackColor = true;
            this._checkBoxDisableMovement.CheckedChanged += new System.EventHandler(this._checkBoxDisableMovement_CheckedChanged);
            // 
            // UserDialogForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(428, 236);
            this.Controls.Add(this._checkBoxDisableMovement);
            this.Controls.Add(this._labelStatus);
            this.Controls.Add(this._checkBoxAutoDefend);
            this.Controls.Add(this._checkBoxSuppressAudio);
            this.Controls.Add(this._buttonStopBot);
            this.Controls.Add(this._buttonContinueProfile);
            this.Controls.Add(this._textBoxMessage);
            this.Name = "UserDialogForm";
            this.Text = "UserDialog";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion


        private void checkBoxAutoDefend_CheckedChanged(object sender, EventArgs e)
        {
            _completionToken.IsAutoDefend = _checkBoxAutoDefend.Checked;
        }


        private void _checkBoxDisableMovement_CheckedChanged(object sender, EventArgs e)
        {
            _completionToken.DisableMovement = _checkBoxDisableMovement.Checked;
        }

        private void buttonContinueProfile_Click(object sender, EventArgs e)
        {
            if (_isStopOnContinue)
            { _completionToken.PopdownResponse = PopdownReason.BotStopViaUser; }
            else
            { _completionToken.PopdownResponse = PopdownReason.ContinueViaUser; }

            Close();
        }


        private void buttonStopBot_Click(object sender, EventArgs e)
        {
            _completionToken.PopdownResponse = PopdownReason.BotStopViaUser;

            Close();
        }


        public abstract class ExpiryActionHandler : EnumBaseType<ExpiryActionHandler>
        {
            public static readonly ExpiryActionHandler HandleInputDisabled_BotStop = new InputDisabled_BotStop();
            public static readonly ExpiryActionHandler HandleInputDisabled_Continue = new InputDisabled_Continue();
            public static readonly ExpiryActionHandler HandleInputDisabled_EnableInput = new InputDisabled_EnableInput();
            public static readonly ExpiryActionHandler HandleInputEnabled_BotStop = new InputEnabled_BotStop();
            public static readonly ExpiryActionHandler HandleInputEnabled_Continue = new InputEnabled_Continue();


            // Caller-visible methods --
            public abstract string ActionAsString();
            public abstract void Initialize(UserDialogForm userDialogForm);
            public abstract void WrapUp(UserDialogForm userDialogForm);

            public static List<string> GetEnumNames() { return GetBaseEnumNames(); }
            public static ReadOnlyCollection<ExpiryActionHandler> GetEnumItems() { return GetBaseEnumItems(); }
            public static ExpiryActionHandler GetEnumItemByName(string name) { return GetBaseEnumItemByName(name); }


            // (Non-visible) specific behaviors --
            private class InputDisabled_BotStop : ExpiryActionHandler
            {
                public override string ActionAsString() { return "Stopping Bot"; }

                public override void Initialize(UserDialogForm userDialogForm)
                {
                    userDialogForm._buttonContinueProfile.Enabled = false;
                    userDialogForm._buttonStopBot.Enabled = false;
                }

                public override void WrapUp(UserDialogForm userDialogForm)
                {
                    userDialogForm._completionToken.PopdownResponse = PopdownReason.BotStopViaExpiry;
                    userDialogForm.Close();
                }
            }

            private class InputDisabled_Continue : ExpiryActionHandler
            {
                public override string ActionAsString() { return "Continuing"; }

                public override void Initialize(UserDialogForm userDialogForm)
                {
                    userDialogForm._buttonContinueProfile.Enabled = false;
                    userDialogForm._buttonStopBot.Enabled = false;
                }

                public override void WrapUp(UserDialogForm userDialogForm)
                {
                    userDialogForm._completionToken.PopdownResponse = PopdownReason.ContinueViaExpiry;
                    userDialogForm.Close();
                }
            }

            private class InputDisabled_EnableInput : ExpiryActionHandler
            {
                public override string ActionAsString() { return "User choice enabled"; }

                public override void Initialize(UserDialogForm userDialogForm)
                {
                    userDialogForm._buttonContinueProfile.Enabled = false;
                    userDialogForm._buttonStopBot.Enabled = false;
                }

                public override void WrapUp(UserDialogForm userDialogForm)
                {

                    userDialogForm._buttonContinueProfile.Enabled = userDialogForm._isBotContinueAllowed;
                    userDialogForm._buttonStopBot.Enabled = userDialogForm._isBotStopAllowed;
                }

            }

            private class InputEnabled_BotStop : ExpiryActionHandler
            {
                public override string ActionAsString() { return "Stopping Bot"; }

                public override void Initialize(UserDialogForm userDialogForm)
                {
                    userDialogForm._buttonContinueProfile.Enabled = true;
                    userDialogForm._buttonStopBot.Enabled = true;
                }

                public override void WrapUp(UserDialogForm userDialogForm)
                {
                    userDialogForm._completionToken.PopdownResponse = PopdownReason.BotStopViaExpiry;
                    userDialogForm.Close();
                }
            }

            private class InputEnabled_Continue : ExpiryActionHandler
            {
                public override string ActionAsString() { return "Continuing"; }

                public override void Initialize(UserDialogForm userDialogForm)
                {
                    userDialogForm._buttonContinueProfile.Enabled = true;
                    userDialogForm._buttonStopBot.Enabled = true;
                }

                public override void WrapUp(UserDialogForm userDialogForm)
                {
                    userDialogForm._completionToken.PopdownResponse = PopdownReason.ContinueViaExpiry;
                    userDialogForm.Close();
                }
            }
        }


        private void dialogForm_FormClosing(object sender, FormClosingEventArgs evt)
        {
            TreeRoot.OnStop -= TreeRoot_OnStop;
            _heartbeatPulseTimer.Enabled = false;
            _labelStatus.Text = "";
        }


        private bool flashed;
        private void heartbeatPulseTimer_Tick(object sender, EventArgs e)
        {

            if (!flashed)
            {
                BringToFront();
                FlashWindowEx(this);
                TopMost = true;
                Focus();
                TopMost = false;
                flashed = true;
            }

            // If we've received a 'pop down' request...
            if (_completionToken.PopdownRequest.IsPopdown())
            {
                _completionToken.PopdownResponse = _completionToken.PopdownRequest;
                Close();
            }

            if (closeRequested)
            {
                _completionToken.PopdownResponse = PopdownReason.UNKNOWN;
                Close();
            }

            // If the expiry timer is ticking...
            // Recall that an _expiryRemainingInSeconds of zero means the timer is not running.
            if (_expiryRemainingInSeconds > 0)
            {
                --_expiryRemainingInSeconds;
                _labelStatus.Text = UtilBuildTimeRemainingStatusText(_expiryActionHandler.ActionAsString(), _expiryRemainingInSeconds);

                if (_expiryRemainingInSeconds <= 0)
                {
                    // An expiry action doesn't always close the dialog --
                    // Sometimes, it will do things like enable inputs that were disabled.
                    _expiryActionHandler.WrapUp(this);
                    _labelStatus.Text = "";
                }
            }


            // If time for audible alert...
            // Recall that a _soundPeriodRemaining of zero means periodic audible alerts are not being issued.
            if (_soundPeriodRemaining > 0)
            {
                --_soundPeriodRemaining;

                if (_soundPeriodRemaining <= 0)
                {
                    if (!_checkBoxSuppressAudio.Checked)
                    { _soundCue.Play(); }

                    _soundPeriodRemaining = _soundCuePeriodInSeconds;
                }
            }
        }


        private static string UtilBuildTimeRemainingStatusText(string actionName,
                                                                 int timeRemaining)
        {
            string formatString = "";
            TimeSpan timeSpan = TimeSpan.FromSeconds(timeRemaining);

            if (timeSpan.Hours > 0)
            { formatString = "{0} in {1:D2}h:{2:D2}m:{3:D2}s."; }
            else if (timeSpan.Minutes > 0)
            { formatString = "{0} in {2:D2}m:{3:D2}s."; }
            else
            { formatString = "{0} in {3:D2}s."; }

            return string.Format(formatString, actionName, timeSpan.Hours, timeSpan.Minutes, timeSpan.Seconds);
        }


        // VS-generated data members
        private System.Windows.Forms.Button _buttonContinueProfile;
        private System.Windows.Forms.Button _buttonStopBot;
        private CheckBox _checkBoxAutoDefend;
        private System.Windows.Forms.CheckBox _checkBoxSuppressAudio;
        private System.Windows.Forms.Timer _heartbeatPulseTimer;
        private Label _labelStatus;
        private System.Windows.Forms.TextBox _textBoxMessage;

        // Hand-written data members
        private AsyncCompletionToken _completionToken;
        private ExpiryActionHandler _expiryActionHandler;
        private int _expiryRemainingInSeconds;
        private readonly bool _isBotStopAllowed;
        private readonly bool _isBotContinueAllowed;
        private readonly bool _isStopOnContinue;
        private readonly SystemSound _soundCue;
        private readonly int _soundCuePeriodInSeconds;
        private int _soundPeriodRemaining;
    }


    [XmlElement("UserDialog")]
    public class UserDialog : ProfileBehavior
    {


        public override bool HighPriority => true;

        // Attributes provided by caller
        [XmlAttribute("DialogText")]
        public string DialogText { get; private set; }

        [XmlAttribute("DialogTitle")]
        [DefaultValue("Attention Required...")]
        public string DialogTitle { get; private set; }




        /// <summary>
        /// InputDisabled_BotStop 
        /// InputDisabled_Continue
        /// InputDisabled_EnableInput
        /// InputEnabled_BotStop
        /// InputEnabled_Continue
        /// </summary>
        [XmlAttribute("ExpiryActionName")]
        [DefaultValue("InputEnabled_Continue")]
        public string ExpiryActionName { get;  set; }

        [XmlAttribute("ExpiryTime")]
        [DefaultValue(0)]
        public int ExpiryTime { get;  set; }

        [XmlAttribute("IsBotStopAllowed")]
        [DefaultValue(true)]
        public bool IsBotStopAllowed { get;  set; }
		
		[XmlAttribute("DisableMovement")]
        [DefaultValue(true)]
        public bool DisableMovement { get;  set; }

		[XmlAttribute("IsAutoDefend")]
        [DefaultValue(true)]
        public bool IsAutoDefend { get;  set; }

        [XmlAttribute("IsBotContinueAllowed")]
        [DefaultValue(false)]
        public bool IsBotContinueAllowed { get; set; }
        

        [XmlAttribute("IsStopOnContinue")]
        [DefaultValue(false)]
        public bool IsStopOnContinue { get;  set; }

        [XmlAttribute("SoundCue")]
        [DefaultValue("Asterisk")]
        public string SoundCueName { get;  set; }
        public SystemSound SoundCue { get;  set; }


        [XmlAttribute("SoundCueInterval")]
        [DefaultValue(60)]
        public int SoundCueIntervalInSeconds { get;  set; }


        /// <summary>
        /// DontCare
        /// Complete
        /// NotComplete
        /// </summary>
        [XmlAttribute("QuestRequirementComplete")]
        public QuestCompleteRequirement QuestRequirementComplete { get;  set; }

        /// <summary>
        /// DontCare
        /// NotInLog
        /// InLog
        /// Available
        /// </summary>
        [XmlAttribute("QuestRequirementInLog")]
        public QuestInLogRequirement QuestRequirementInLog { get;  set; }


        // Private variables for internal state
        private Composite _behavior;
        private AsyncCompletionToken _completionToken;
        //private ConfigMemento _configMemento;
        private bool _isBehaviorDone;

        private void UserDialogExitProcessing(PopdownReason popdownReason)
        {
            // Log to rebornbuddy why we're exiting behavior...
            if (popdownReason.IsReasonKnown())
            {
                string directiveRequester = IsStopOnContinue ? "Profile Writer request"
                    : popdownReason.IsPopdown() ? "Notification criteria no longer valid"
                    : popdownReason.IsTimerExpiry() ? "Profile Writer request"
                    : popdownReason.IsUserResponse() ? "User request"
                    : "Profile Writer request";
                string messageType = popdownReason.IsTimerExpiry() ? "timer expired"
                    : popdownReason.IsUserResponse() ? "user response"
                    : popdownReason.IsPopdown() ? "completion criteria"
                    : "info";
                string terminationMessage = string.Format("{0} {1}",
                                                                    popdownReason.IsBotStop() ? "Rebornbuddy stopped due to "
                                                                        : "Continuing profile due to",
                                                                     directiveRequester);

                TreeRoot.StatusText = terminationMessage;

                DialogText = DialogText.Replace(@"\n", System.Environment.NewLine).Replace(@"\t", "\t");
                Logging.WriteDiagnostic("[{0}, {1}] {2}\nDisposition: {3}", DialogTitle, messageType, DialogText, terminationMessage);
            }

            if (popdownReason.IsBotStop())
            { TreeRoot.Stop(); }
        }

        #region hb import

        #region QuestCompleteRequirement enum
        /// <summary>Values that represent QuestCompleteRequirement.</summary>
        public enum QuestCompleteRequirement
        {
            /// <summary>An enum constant representing the dont care option.</summary>
            DontCare,
            /// <summary>An enum constant representing the complete option.</summary>
            Complete,
            /// <summary>An enum constant representing the not complete option.</summary>
            NotComplete,
        }
        #endregion

        #region QuestInLogRequirement enum
        /// <summary>Values that represent QuestInLogRequirement.</summary>
        public enum QuestInLogRequirement
        {
            /// <summary>An enum constant representing the dont care option.</summary>
            DontCare,
            /// <summary>An enum constant representing the in log option.</summary>
            InLog,
            /// <summary>An enum constant representing the not in log option.</summary>
            NotInLog,
            Available
        }
        #endregion

        /// <summary>Determine whether a behavior should start or continue based on the QuestId, and its
        /// required presence and completion criteria.</summary>
        /// <param name="questId">                 provides the reference for which the specified
        /// qualifies should be applied.</param>
        /// <param name="questInLogRequirement">   the QuestId must meet this specified qualifier for
        /// the behavior to proceed.</param>
        /// <param name="questCompleteRequirement">the QuestId must mee this specified qualifier for the
        /// behavior to proceed.</param>
        /// <returns>true, if the provided QuestId meets the specified qualifiers; otherwise, returns
        /// false.</returns>
        public bool UtilIsProgressRequirementsMet(uint questId,QuestInLogRequirement questInLogRequirement,QuestCompleteRequirement questCompleteRequirement)
        {
  

            // QuestId zero always meets our requirements, by definition			
            if (questId == 0)
            {
                return true;
            }


            var quest = QuestLogManager.GetQuestById(questId);

            // 'Quest In Log' handling --
            if (questInLogRequirement == QuestInLogRequirement.InLog && quest == null)
            { return false; }

            if (questInLogRequirement == QuestInLogRequirement.NotInLog && quest != null)
            { return false; }

            if (questInLogRequirement == QuestInLogRequirement.Available && ConditionParser.IsQuestAcceptQualified((int)questId))
            { return false; }


            // 'Quest Complete' handling --
            bool isQuestComplete = QuestLogManager.IsQuestCompleted(questId);
            bool isQuestFailed = false;

            if (questCompleteRequirement == QuestCompleteRequirement.Complete && !isQuestComplete)
            { return false; }

            if (questCompleteRequirement == QuestCompleteRequirement.NotComplete && isQuestComplete)
            { return false; }


            if (IsStepComplete)
                return false;

            return true;
        }


        #endregion

        #region Overrides of CustomForcedBehavior


        private async Task<bool> HandleDeath()
        {
            if (Core.Player.IsDead)
            {
                Log("Died, waiting for respawn");
                await Coroutine.Wait(Timeout.Infinite, () => !Core.Player.IsDead);
            }
            return true;
        }
        private bool partyInCombat
        {
            get
            {
                foreach (var partyMember in PartyManager.VisibleMembers)
                {
                    if (partyMember.BattleCharacter.InCombat)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private async Task<bool> SkipCutscene()
        {
            if (!OrderBot.BlockSkippingCutscenes)
            {
                if (OrderBotSettings.Instance.SkipCutScenes)
                {
                    if (AgentCutScene.Instance.CanSkip && !SelectString.IsOpen)
                    {
                        AgentCutScene.Instance.PromptSkip();
                        if (await Coroutine.Wait(600, () => SelectString.IsOpen))
                        {
                            SelectString.ClickSlot(0);
                            await Coroutine.Sleep(1000);
                        }
                    }
                }
            }

            return true;
        }

        private async Task<bool> Behavior()
        {
            while (true)
            {
                bool isDialogComplete = _completionToken.IsComplete;
                bool isProgressing = UtilIsProgressRequirementsMet((uint)QuestId, QuestRequirementInLog, QuestRequirementComplete);

                await SkipCutscene();

                // We're complete... wrap it up
                if (isDialogComplete || !isProgressing)
                {
                    // If we're no longer progressing, we don't want to wait for the user response to close up shop...
                    // Thus, we use UNKNOWN when not progressing.  This will also keep us from blocking and waiting for
                    // PopdownResponse to be populated by the UserDialogForm we're trying to close.
                    UserDialogExitProcessing(isDialogComplete
                        ? _completionToken.PopdownResponse
                        : PopdownReason.PopdownCompletionCriteriaMet);

                    _isBehaviorDone = true;
                    return true;
                }

                // If 'auto defend' is on and we're in combat, we skip this node to allow combat to take place
                // somewhere in our parent's subtree
                if (_completionToken.IsAutoDefend && (Core.Me.InCombat || partyInCombat))
                    return false;

                // 'auto defend is off'...
                // RunStatus.Running returns us to this node.  This allows the user to control everything manually--
                // including combat
                await Coroutine.Yield();
            }
        }


        protected override Composite CreateBehavior()
        {
            if (_behavior == null)
            {
                _behavior = new ActionRunCoroutine(ret=>Behavior());
            }

            return _behavior;
        }


        private static NavigationProvider _navigationProvider;
        internal static void DisplaceNavigationProvider()
        {
            if (_navigationProvider == null)
            {
                _navigationProvider = Navigator.NavigationProvider;
                Navigator.NavigationProvider = new NullProvider();
            }

        }

        internal static void RestoreNavigationProvider()
        {
            if (_navigationProvider != null)
            {
                Navigator.NavigationProvider = _navigationProvider;
                _navigationProvider = null;
            }
        }

        private Composite reiveLogic;
        protected override void OnStart()
        {
            reiveLogic = new ActionRunCoroutine(r => HandleDeath());
            TreeHooks.Instance.AddHook("DeathReviveLogic", reiveLogic);
            try
            {
                string[] expiryActionNames = UserDialogForm.ExpiryActionHandler.GetEnumNames().ToArray();
                Dictionary<string, System.Media.SystemSound> soundsAllowed = new Dictionary<string, System.Media.SystemSound>()
                                                                 {
                                                                      { "Asterisk",       System.Media.SystemSounds.Asterisk },
                                                                      { "Beep",           System.Media.SystemSounds.Beep },
                                                                      { "Exclamation",    System.Media.SystemSounds.Exclamation },
                                                                      { "Hand",           System.Media.SystemSounds.Hand },
                                                                      { "Question",       System.Media.SystemSounds.Question },
                                                                 };

                SoundCue = soundsAllowed[SoundCueName];
            }
            catch (Exception except)
            {

            }



            // If the quest is complete, this behavior is already done...
            // So we don't want to falsely inform the user of things that will be skipped.
            if (!IsDone)
            {
                //_configMemento = new ConfigMemento();

                _completionToken = new AsyncCompletionToken(Core.Me.Name,
                                                            DialogTitle,
                                                            DialogText,
                                                            ExpiryActionName,
                                                            ExpiryTime,
                                                            IsBotStopAllowed,
                                                            IsBotContinueAllowed,
                                                            IsStopOnContinue,
                                                            SoundCue,
                                                            SoundCueIntervalInSeconds);
				_completionToken.DisableMovement = (DisableMovement);
				_completionToken.IsAutoDefend = (IsAutoDefend);
                // this.UpdateGoalText(QuestId, "User Attention Required...");
                TreeRoot.StatusText = "Waiting for user dialog to close";
            }
        }


        protected override void OnDone()
        {
            if (_completionToken != null)
            {
                _completionToken.Dispose();
                _completionToken = null;
            }

            TreeHooks.Instance.RemoveHook("DeathReviveLogic", reiveLogic);
            RestoreNavigationProvider();

            UserDialogExitProcessing(PopdownReason.UNKNOWN);
            //TreeRoot.GoalText = string.Empty;
            TreeRoot.StatusText = string.Empty;
            base.OnDone();
        }


        public override bool IsDone
        {
            get
            {
                // normal completion
                return _isBehaviorDone || !UtilIsProgressRequirementsMet((uint)QuestId, QuestRequirementInLog, QuestRequirementComplete);
            }
        }




        #endregion
    }


    //============================================================
    // The remainder of this file are support classes to get the work done --
    //

    // We know this is clumsy, but its a .NET-ism --
    // Form widgets can *only* be controlled by the thread that creates them.
    // Thus, we make this guarantee by wrapping all creation & processing actions
    // in a method called by just one thread.  <sigh>
    internal class AsyncCompletionToken : IDisposable
    {
        public AsyncCompletionToken(string toonName,
                                    string dialogTitle,
                                    string dialogMessage,
                                    string expiryActionName,
                                    int expiryRemainingInSeconds,
                                    bool isBotStopAllowed,
                                    bool isBotContinueAllowed,
                                    bool isStopOnContinue,
                                    SystemSound soundCue,
                                    int soundPeriodInSeconds)
        {
            _dialogDelegate = new UserDialogDelegate(PopupDialogViaDelegate);
            _isAutoDefend = true;
            _popdownRequest = PopdownReason.UNKNOWN;
            _popdownResponse = PopdownReason.UNKNOWN;

            _actResult = _dialogDelegate.BeginInvoke(this,
                                                     toonName,
                                                     dialogTitle,
                                                     dialogMessage,
                                                     expiryActionName,
                                                     expiryRemainingInSeconds,
                                                     isBotStopAllowed,
                                                     isBotContinueAllowed,
                                                     isStopOnContinue,
                                                     soundCue,
                                                     soundPeriodInSeconds,
                                                     null,
                                                     null);
        }


        public void Dispose()
        {
            PopdownRequest = PopdownReason.PopdownAndDispose;
            WaitForComplete();
        }




        // We know this is clumsy, but its a .NET-ism --
        // Form widgets can *only* be controlled by the thread that creates them.
        // Thus, we make this guarantee by wrapping all creation & processing actions
        // in a method called by just one thread.  <sigh>
        private static void PopupDialogViaDelegate(AsyncCompletionToken completionToken,
                                                   string toonName,
                                                   string dialogTitle,
                                                   string dialogMessage,
                                                   string expiryActionName,
                                                   int expiryRemainingInSeconds,
                                                   bool isBotStopAllowed,
                                                   bool isBotContinueAllowed,
                                                   bool isStopOnContinue,
                                                   SystemSound soundCue,
                                                   int soundPeriodInSeconds)
        {
            UserDialogForm dialogForm = new UserDialogForm(completionToken,
                                                                     toonName,
                                                                     dialogTitle,
                                                                     dialogMessage,
                                                                     expiryActionName,
                                                                     expiryRemainingInSeconds,
                                                                     isBotStopAllowed,
                                                                     isBotContinueAllowed,
                                                                     isStopOnContinue,
                                                                     soundCue,
                                                                     soundPeriodInSeconds);

            // Popup the window--
            // We'd *really* like to make this dialog a child of the main Rebornbuddy window.
            // By doing such, the dialog would be 'brought to the front' any time te Rebornbuddy main
            // window was.
            // Alas, C#/WindowsForms disallows this because the main RB GUI and this dialog are
            // on separate threads.
            //dialogForm.TopMost = true;
            dialogForm.Activate();
            dialogForm.ShowDialog();
            

            //Core.SetFocus(new HandleRef(null, dialogForm.Handle));
        }


        // IsAutoDefend is updated by UserDialogForm any time the user alters the corresponding
        // checkbox on the UserDialogForm.
        public bool IsAutoDefend
        {
            get { lock (_stateLock) { return _isAutoDefend; } }
            set {
                lock (_stateLock)
                {
                    if (!value)
                    {
                        if (Poi.Current != null)
                            Poi.Clear("Cleaing poi cause toggle");
                    }


                    _isAutoDefend = value;
                } }
        }
        public bool DisableMovement
        {
            get { lock (_stateLock) { return _movementDisabled; } }
            set
            {
                lock (_stateLock)
                {
                    if (value)
                    {
                        UserDialog.DisplaceNavigationProvider();
                    }
                    else
                    {
                        UserDialog.RestoreNavigationProvider();
                    }


                    _movementDisabled = value;
                }
            }
        }

        

        // This is a request to the UserDialogForm, asking it to close
        // for the provided reason.
        // Note that UserDialogForm may pop down for other reasons, such
        // as the user clicking a button, or the expiry timer elapsing.
        public PopdownReason PopdownRequest
        {
            get { lock (_stateLock) { return _popdownRequest; } }
            set { lock (_stateLock) { _popdownRequest = value; } }
        }


        // This is a response from the UserDialogForm, making available
        // the reason that it popped down.  If the PopdownResponse is not
        // yet available, accessing this value will block the caller until
        // it becomes available.  If the caller doesn't want to be blocked,
        // use the IsComplete property to determine whether or not to
        // read this one.
        public PopdownReason PopdownResponse
        {
            get
            {
                WaitForComplete();

                lock (_stateLock) { return _popdownResponse; }
            }
            set { lock (_stateLock) { _popdownResponse = value; } }
        }


        public bool IsComplete
        {
            get { return _actResult.IsCompleted; }
        }

        public void WaitForComplete()
        {
            if (!IsComplete)
                _dialogDelegate.EndInvoke(_actResult);
        }


        private IAsyncResult _actResult;
        private UserDialogDelegate _dialogDelegate;
        private bool _isAutoDefend;
        private bool _movementDisabled;
        private PopdownReason _popdownRequest;
        private PopdownReason _popdownResponse;
        private readonly object _stateLock = new object();
    }


    internal enum PopdownReason
    {
        BotStopViaExpiry,
        BotStopViaUser,
        ContinueViaExpiry,
        ContinueViaUser,
        PopdownAndDispose,
        PopdownCompletionCriteriaMet,
        UNKNOWN,
    };

    internal static class PopdownReason_ExtensionMethods
    {
        public static bool IsBotStop(this PopdownReason popdownReason)
        {
            return popdownReason == PopdownReason.BotStopViaExpiry
                   || popdownReason == PopdownReason.BotStopViaUser;
        }

        public static bool IsContinue(this PopdownReason popdownReason)
        {
            return popdownReason == PopdownReason.ContinueViaExpiry
                   || popdownReason == PopdownReason.ContinueViaUser;
        }

        public static bool IsReasonKnown(this PopdownReason popdownReason)
        {
            return popdownReason != PopdownReason.UNKNOWN;
        }

        public static bool IsPopdown(this PopdownReason popdownReason)
        {
            return popdownReason == PopdownReason.PopdownAndDispose
                   || popdownReason == PopdownReason.PopdownCompletionCriteriaMet;
        }

        public static bool IsTimerExpiry(this PopdownReason popdownReason)
        {
            return popdownReason == PopdownReason.BotStopViaExpiry
                   || popdownReason == PopdownReason.ContinueViaExpiry;
        }


        public static bool IsUserResponse(this PopdownReason popdownReason)
        {
            return popdownReason == PopdownReason.BotStopViaUser
                   || popdownReason == PopdownReason.ContinueViaUser;
        }
    }


    internal delegate void UserDialogDelegate(AsyncCompletionToken completionToken,
                                            string toonName,
                                            string dialogTitle,
                                            string dialogMessage,
                                            string expiryActionName,
                                            int expiryRemainingInSeconds,
                                            bool isBotStopAllowed,
                                            bool isBotContinueAllowed,
                                            bool isStopOnContinue,
                                            SystemSound soundCue,
                                            int soundPeriodInSeconds);


    //============================================================
    // Candidate functionality for CustomForcedBehavior base class --


    // using SystemCollections.ObjectModel

    /// <summary>
    /// Base class for the classic enumeration pattern.  The enumeration pattern allows
    /// behavior (e.g., methods) to be associated with each enumerable item that is
    /// defined.
    ///
    /// The enumeration pattern has these characteristics:
    /// * This technique is *fast*--no reflection is involved
    /// * Any number of methods can be associatd with each enumerated item
    /// * Strongly typed
    /// * Enforces type safety against accidental misuse
    /// * Can be used in If statements and iterated over
    /// * Cannot be used in a switch statement. This is a feature--switch statements
    ///   are not object-oriented and a major source of maintenance errors.
    ///   As enumerations are added, 'default' cases in switch statements mask
    ///   the omission of their handling.
    ///
    ///  A good tutorial on this pattern can be found here...
    ///      http://www.codeproject.com/KB/cs/EnhancedEnums.aspx
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class EnumBaseType<T> where T : EnumBaseType<T>
    {
        protected EnumBaseType()
        {
            s_enumValues.Add(this.GetType().Name, (T)this);
        }

        public static List<string> GetBaseEnumNames()
        {
            return s_enumValues.Keys.ToList();
        }

        public static ReadOnlyCollection<T> GetBaseEnumItems()
        {
            return s_enumValues.Values.ToList().AsReadOnly();
        }

        public static T GetBaseEnumItemByName(string name)
        {
            T returnValue;

            if (s_enumValues.TryGetValue(name, out returnValue))
            { return returnValue; }

            throw new ArgumentException(string.Format("Invalid enum item name ('{0}') provided for {1}",
                name,
                typeof(T).Name));
        }

        private static Dictionary<string, T> s_enumValues = new Dictionary<string, T>();
    }
}
