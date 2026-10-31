using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TypeTrail {
    // One controller owns the state; this file contains its input responsibilities.
    internal sealed partial class Controller {
        void RefreshFocus(bool objectEvent, int obj, int child) {
            IntPtr foreground = Native.GetForegroundWindow();
            uint processId; uint thread = Native.GetWindowThreadProcessId(foreground, out processId);
            var gui = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            IntPtr focused = Native.GetGUIThreadInfo(thread, ref gui) ? gui.hwndFocus : IntPtr.Zero;
            ApplyFocus(foreground,focused,objectEvent,obj,child);
        }
        void ApplyFocus(IntPtr foreground,IntPtr focused,bool objectEvent,int obj,int child) {
            bool changed = foreground != observedWindow || focused != observedFocus;
            // Virtual child IDs also change while an IME candidate is selected.
            // The reader's runtime ID identifies real editor changes instead.
            if (changed) {
                if(observedWindow!=IntPtr.Zero && typing.Text.Length>0) {
                    if(retiredTyping.Count>=64)retiredTyping.Remove(retiredTyping.OrderBy(p=>p.Value.At).First().Key);
                    var retired=new RetiredTyping();retired.Evidence.Append(typing.Text);
                    retiredTyping[Native.GetAncestor(observedWindow,2)]=retired;
                }
                pendingPaste = null; pasteHints.Reset(); nextPasteBaseline=null; assemblingPasteBaseline=null; pendingPasteBaseline=null; pendingPasteConsumed=0;
                focusSerial++; lastInput = 0; lastImeCommit = 0; typing.Clear(); inputCanInsert = false;
                Trace("focus " + focusSerial + " window=" + foreground + " control=" + focused + " object=" + obj + " child=" + child);
                observedWindow = foreground; observedFocus = focused;
                observedObject = objectEvent ? obj : 0; observedChild = objectEvent ? child : 0;
                haveFocusObject = objectEvent;
                if (!nativeFocusPending) {
                    nativeFocusPending = true;
                    ui.BeginInvoke(DispatcherPriority.Send, new Action(delegate {
                        nativeFocusPending = false;
                        if (!disposed && !settings.Paused) RefreshNativeCapture(true);
                    }));
                }
            } else if (objectEvent) { observedObject = obj; observedChild = child; haveFocusObject = true; }
        }
        void SaveFieldTyping() {
            if(protectionEpoch==0 || typing.Text.Length==0)return;
            if(fieldTyping.Count>=128)fieldTyping.Remove(fieldTyping.Keys.Min());
            var snapshot=new TypingEvidence();snapshot.Append(typing.Text);fieldTyping[protectionEpoch]=snapshot;
        }
        void EnsureProtectionFocus(IntPtr root,IntPtr focused,uint owner,ulong time,bool notification) {
            if(root==IntPtr.Zero || owner==(uint)pid)return;
            SaveFieldTyping();
            long next=fieldAdmission.Focus(owner,unchecked((ulong)root.ToInt64()),unchecked((ulong)focused.ToInt64()),time,notification);
            if(next!=protectionEpoch) {protectionEpoch=next;focusDirty=true;protectedWindow=IntPtr.Zero;}
        }
        void ProcessFocusInput(KeyboardInput input) {
            if(settings.Paused || input.ProcessId==(uint)pid || input.Window==IntPtr.Zero)return;
            if(input.FocusKind==0x800E) {focusDirty=true;return;}
            SaveFieldTyping();
            bool same=input.Window==observedWindow && input.Focus==observedFocus;
            ApplyFocus(input.Window,input.Focus,true,input.ObjectId,input.ChildId);
            EnsureProtectionFocus(input.Window,input.Focus,input.ProcessId,input.Tick,true);
            if(same) {
                // Virtual DOM fields share a native HWND. Keep their pending
                // keyboard evidence separate while metadata identifies the field.
                typing.Clear();pendingPaste=null;pendingPasteBaseline=null;pasteHints.Reset();
                nextPasteBaseline=null;assemblingPasteBaseline=null;pendingPasteConsumed=0;
                focusSerial++;lastInput=0;lastImeCommit=0;inputCanInsert=false;
            }
            focusDirty=true;DrainAdmitted();
        }
        void DrainKeyboardInput() {
            if(keyboardMonitor==null || drainingKeyboard)return;
            drainingKeyboard=true;
            try {
                foreach(var input in keyboardMonitor.Drain())ProcessKeyboard(input);
                int dropped=keyboardMonitor.Dropped;
                if(dropped!=observedKeyboardDropped) {
                    captureStatus=L.T("输入过快，部分字符未能保存");captureState="partial";
                } else if(keyboardMonitor.Error!=null && captureState!="partial") {
                    captureStatus=L.T("输入活动检测未能启动，请退出后重新打开拾字。");captureState="capture_error";
                }
                observedKeyboardDropped=dropped;
            }finally{drainingKeyboard=false;}
        }
        void ProcessKeyboard(KeyboardInput input) {
            if(input.FocusEvent) {ProcessFocusInput(input);return;}
            if(!settings.Paused && input.Window!=IntPtr.Zero) {
                if(input.ProcessId==(uint)pid)return;
                int key=input.Key;
                bool ctrl=input.Ctrl,alt=input.Alt,win=input.Win,shift=input.Shift,altGr=input.AltGr;
                SaveFieldTyping();ApplyFocus(input.Window,input.Focus,false,0,0);
                EnsureProtectionFocus(input.Window,input.Focus,input.ProcessId,input.Tick,false);
                uint inputPid=input.InputProcessId==0?input.ProcessId:input.InputProcessId;
                if(input.ProcessId==pid)return;
                if(!fieldAdmission.IsDenied(protectionEpoch))typing.Key(key,input.Scan,ctrl,alt,win,input.Layout,altGr,input.KeyState(),input.Tick);
                bool pasteCommand = KeyboardInputPolicy.IsPaste(key, ctrl, alt, win, shift);
                if (KeyboardInputPolicy.InvalidatesPaste(key, ctrl, alt, win, shift)) { pendingPaste = null; pendingPasteSerial = -1; pendingPasteBaseline=null; }
                if (KeyboardInputPolicy.Modifies(key, ctrl, alt, win, shift, altGr)) {
                    lastInput = input.Timestamp; lastInputTick=input.Tick; lastInputWindow = input.Window;
                    lastInputProcessId=inputPid;lastInputTarget=input.Focus==IntPtr.Zero?input.Window:input.Focus;
                    inputSerial++; inputFocusSerial = focusSerial;
                    if(!input.ActivityPulsed)nativeCapture.NotifyInputAt(inputPid,lastInputTarget,input.Tick);
                    inputCanInsert |= pasteCommand || ((!ctrl || altGr) && key != 8 && key != 46);
                    if(!fieldAdmission.IsDenied(protectionEpoch) && (pasteCommand || ((!ctrl || altGr) && key!=8 && key!=9 && key!=13 && key!=32 && key!=46)))
                        keyboardActivity.Record(input.ProcessId,unchecked((ulong)input.Window.ToInt64()),unchecked((ulong)(input.Focus==IntPtr.Zero?input.Window:input.Focus).ToInt64()),protectionEpoch,fieldAdmission.FieldFor(protectionEpoch),input.Tick,input.Timestamp);
                    if (pasteCommand) {
                        long pasteFocus = focusSerial;
                        long pasteSerial = inputSerial;
                        DateTime pasteAt=DateTime.UtcNow-TimeSpan.FromSeconds(Math.Max(0,Stopwatch.GetTimestamp()-input.Timestamp)/(double)Stopwatch.Frequency);
                        pendingPaste = null; pendingPasteBaseline=null; pendingPasteConsumed=0; pendingPasteSerial = pasteSerial; typing.Clear();
                        // Read only on an actual paste command, outside the keyboard
                        // hook. The text is still required to appear in the editor.
                        ui.BeginInvoke(DispatcherPriority.Send, new Action(delegate {
                            if (pasteFocus != focusSerial || pendingPasteSerial != pasteSerial || disposed) return;
                            try {
                                if (Clipboard.ContainsText()) {
                                    // Keep clipboard evidence separate from the
                                    // short keyboard hint, including long pastes.
                                    pendingPaste = History.Tail(History.Normalize(Clipboard.GetText()), settings.CharacterLimit);
                                    pendingPasteFocus = pasteFocus;
                                    pendingPasteBaseline=null; pendingPasteConsumed=0;
                                    pendingPasteSerial = pasteSerial; pendingPasteAt = pasteAt;
                                }
                            } catch {}
                        }));
                    }
                    if (key == 32 || key == 13 || (key >= 48 && key <= 57)) {
                        if ((input.Layout.ToInt64() & 0x3FF) == 4) lastImeCommit = lastInput;
                        if(!ctrl && !alt && !win && !fieldAdmission.IsDenied(protectionEpoch))
                            imeConversions.Select(input.ProcessId,unchecked((ulong)input.Window.ToInt64()),unchecked((ulong)lastInputTarget.ToInt64()),protectionEpoch,input.Tick,input.Layout,key,typing.Text);
                    }
                }
            }
        }
        bool InputActive() { return lastInput != 0 && inputFocusSerial == focusSerial && (Stopwatch.GetTimestamp() - lastInput) / (double)Stopwatch.Frequency < 5 && lastInputWindow == Native.GetForegroundWindow(); }
        void ReceiveCommitted(CommittedInput input, string sourceName) {
            DrainKeyboardInput();
            if(settings.Paused)return;
            if(input.Kind==4 || input.Kind==5) {ApplyCommitted(input,sourceName,null,0);return;}
            IntPtr root=Native.GetAncestor(new IntPtr(unchecked((long)input.Window)),2);
            if(root==IntPtr.Zero)root=new IntPtr(unchecked((long)input.Window));
            uint rootPid;Native.GetWindowThreadProcessId(root,out rootPid);if(rootPid==0)rootPid=input.ProcessId;
            bool safe=NativeInputEvidence.SafeForAdmission(input);
            var nativeClass=new StringBuilder(256);Native.GetClassName(new IntPtr(unchecked((long)input.Window)),nativeClass,nativeClass.Capacity);bool rich;
            string stable=NativeEditCapture.IsEditClass(nativeClass.ToString(),out rich)?"native:"+input.Window:null;
            fieldAdmission.Offer(input,sourceName,rootPid,unchecked((ulong)root.ToInt64()),safe,stable);
            if(fieldAdmission.HasPending)focusDirty=true;
            DrainAdmitted();
        }
        void DrainAdmitted(bool finish=false) {
            foreach(var item in fieldAdmission.Drain(Native.GetTickCount64(),finish)) {
                string key=item.Input.ProcessId+":"+item.Input.Window;
                var nativeClass=new StringBuilder(256);Native.GetClassName(new IntPtr(unchecked((long)item.Input.Window)),nativeClass,nativeClass.Capacity);bool rich;
                if(NativeEditCapture.IsEditClass(nativeClass.ToString(),out rich))item.Field="native:"+item.Input.Window;
                string token;
                if(fieldTokens.TryGetValue(item.Epoch,out token)) {
                    if(token.StartsWith("epoch:",StringComparison.Ordinal) && !item.Field.StartsWith("epoch:",StringComparison.Ordinal)) {
                        bool moved=true;
                        foreach(var actor in journalEpochs.Where(p=>p.Value==item.Epoch).ToList()) {
                            string[] parts=actor.Key.Split(':');uint owner;ulong windowId;
                            if(parts.Length==2 && UInt32.TryParse(parts[0],out owner) && UInt64.TryParse(parts[1],out windowId))moved=directHistory.RebindField(owner,windowId,token,item.Field)&&moved;
                        }
                        if(moved) {token=item.Field;fieldTokens[item.Epoch]=token;}
                    }
                } else {
                    if(fieldTokens.Count>=128)fieldTokens.Remove(fieldTokens.Keys.Min());
                    token=item.Field;fieldTokens[item.Epoch]=token;
                }
                item.Field=token;item.Input.FieldIdentity=token;
                if(journalFields.Count>=256 && !journalFields.ContainsKey(key)) {string remove=journalFields.Keys.First();journalFields.Remove(remove);journalEpochs.Remove(remove);}
                journalFields[key]=item.Field;journalEpochs[key]=item.Epoch;
                TypingEvidence evidence=null;if(item.Epoch!=protectionEpoch)fieldTyping.TryGetValue(item.Epoch,out evidence);
                ApplyCommitted(item.Input,item.Source,evidence,item.Epoch);
            }
            if(fieldAdmission.TakeLoss()) {captureStatus=L.T("输入边界暂未确认，部分字符未能保存");captureState="partial";}
        }
        void ApplyCommitted(CommittedInput input,string sourceName,TypingEvidence admittedEvidence,long admittedEpoch) {
            PasteBaseline priorBaseline=nextPasteBaseline; nextPasteBaseline=null;
            IntPtr inputRoot = Native.GetAncestor(new IntPtr(unchecked((long)input.Window)), 2);
            if (settings.Paused)return;
            bool current = observedWindow!=IntPtr.Zero && inputRoot==Native.GetAncestor(observedWindow,2) && (admittedEpoch==0 || protectionEpoch==0 || admittedEpoch==protectionEpoch);
            if(input.Kind==5) {
                if(current && (observedFocus==IntPtr.Zero || input.Window==unchecked((ulong)observedFocus.ToInt64()))) nextPasteBaseline=PasteBaseline.Decode(input);
                return;
            }
            if (input.Kind == 4) {
                // A hook snapshots the clipboard during the target's paste
                // command, before temporary clipboard contents are restored.
                // A complete hint still requires a matching editor insertion.
                if(!current || (observedFocus!=IntPtr.Zero && input.Window!=unchecked((ulong)observedFocus.ToInt64()))) { pasteHints.Reset(); return; }
                string paste;
                if((input.Flags&256)!=0) {
                    pendingPaste=null; pendingPasteBaseline=null; pendingPasteConsumed=0;
                    assemblingPasteBaseline=priorBaseline!=null && priorBaseline.Matches(input)?priorBaseline:null;
                }
                if(pasteHints.Append(input,500000,out paste)) {
                    pendingPaste=paste; pendingPasteFocus=focusSerial;
                    pendingPasteBaseline=assemblingPasteBaseline; assemblingPasteBaseline=null; pendingPasteConsumed=0;
                    pendingPasteSerial=++inputSerial; pendingPasteAt=DateTime.UtcNow;
                    lastInput=Stopwatch.GetTimestamp(); lastInputWindow=observedWindow;
                    inputFocusSerial=focusSerial; inputCanInsert=true;
                }
                return;
            }
            TypingEvidence evidence=admittedEvidence??(current?typing:null);
            RetiredTyping retired;
            if(!current && retiredTyping.TryGetValue(inputRoot,out retired) && (DateTime.UtcNow-retired.At).TotalSeconds<10)evidence=retired.Evidence;
            bool pasteEvidence = current && pendingPaste != null && pendingPasteFocus == focusSerial &&
                (DateTime.UtcNow-pendingPasteAt).TotalSeconds <= 3;
            int consumed;
            bool selectorConsumed=false;
            bool verified = NativeInputEvidence.Verify(input, pasteEvidence?pendingPaste:(evidence==null?"":evidence.Text), out consumed);
            if (!verified && pasteEvidence) {
                pasteEvidence = false;
                verified = NativeInputEvidence.Verify(input, evidence==null?"":evidence.Text, out consumed);
            }
            if(!verified && evidence!=null) {
                uint owner;Native.GetWindowThreadProcessId(inputRoot,out owner);
                bool activity=keyboardActivity.Has(owner,unchecked((ulong)inputRoot.ToInt64()),input.Window,input.FieldIdentity,input.Tick,fieldAdmission.FieldFor,fieldAdmission.IsDenied);
                if(imeConversions.AcceptSeparator(input,owner,unchecked((ulong)inputRoot.ToInt64()),admittedEpoch)) {
                    verified=true;consumed=0;selectorConsumed=true;
                    Trace("commit literal-space pid="+input.ProcessId+" flags="+input.Flags+" serial="+input.InputSerial);
                } else if(imeConversions.Accept(input,owner,unchecked((ulong)inputRoot.ToInt64()),admittedEpoch,activity)) {
                    verified=true;consumed=0;selectorConsumed=true;evidence.ConsumeBefore(input.Tick);
                    Trace("commit converted-ime pid="+input.ProcessId+" flags="+input.Flags+" serial="+input.InputSerial+" length="+(input.Text??"").Length);
                }
            }
            if (!verified) { Trace("commit skipped-evidence pid="+input.ProcessId+" flags="+input.Flags+" length="+(input.Text??"").Length+" hintLength="+(evidence==null?0:evidence.Text.Length));return; }
            uint acceptedOwner;Native.GetWindowThreadProcessId(inputRoot,out acceptedOwner);
            if(!selectorConsumed)imeConversions.ObserveAccepted(input,acceptedOwner,unchecked((ulong)inputRoot.ToInt64()),admittedEpoch);
            if(current && input.Kind==1 && (input.Flags&32)!=0 && (input.InputSerial!=0 || (input.Flags&1024)!=0)) {
                // A native before/after proof covers this entire paste, even if
                // another provider already supplied a differently-sized prefix.
                pendingPaste=null;pendingPasteBaseline=null;pendingPasteConsumed=0;
                pasteEvidence=false;
            }
            if(!pasteEvidence && evidence!=null)evidence.Consume(consumed);
            if (current && pendingPaste != null && input.Kind == 1 && input.Text != null && pendingPaste.StartsWith(input.Text, StringComparison.Ordinal)) {
                pendingPaste = pendingPaste.Substring(input.Text.Length);
                pendingPasteConsumed+=input.Text.Length;
                if (pendingPaste.Length == 0) {pendingPaste = null;pendingPasteBaseline=null;pendingPasteConsumed=0;}
            }
            if (evidence!=null && (input.Flags & (2 | 8)) != 0) evidence.ConsumeBefore(input.Tick);
            if((input.Flags&16)==0 && input.Kind==1) {
                IntPtr root=Native.GetAncestor(new IntPtr(unchecked((long)input.Window)),2);uint owner;Native.GetWindowThreadProcessId(root,out owner);
                keyboardActivity.Consume(owner,unchecked((ulong)root.ToInt64()),input.Window,input.FieldIdentity,admittedEpoch,input.Tick,fieldAdmission.FieldFor,0,false);
            }
            if (directHistory.Apply(input, sourceName)) { dirty = true; listDirty = true; captureStatus = sourceName + L.T(" · 已记录上屏文字"); captureState = "ok";Trace("commit saved pid="+input.ProcessId+" flags="+input.Flags+" length="+(input.Text??"").Length+" entries="+history.Entries.Count); }
        }
        void ResetFieldAdmission() {
            fieldAdmission.Reset();keyboardActivity.Clear();imeConversions.Clear();fieldTyping.Clear();journalFields.Clear();journalEpochs.Clear();fieldTokens.Clear();protectionEpoch=0;protectedWindow=IntPtr.Zero;
        }
    }
}
