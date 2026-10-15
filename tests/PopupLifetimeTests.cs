using System;
using System.Drawing;
using CodexCompletionReminder;

internal static class PopupLifetimeTests
{
    public static int Main()
    {
        try {
            object oldPopup=new object(), newPopup=new object();
            object current=oldPopup;
            int dismissals=0;
            Action delayed=delegate { if(PopupCallbackGate.IsCurrent(oldPopup,current)) dismissals++; };
            current=newPopup;
            delayed();
            if(dismissals!=0) throw new Exception("Old queued input dismissed replacement popup");
            if(PopupCallbackGate.IsCurrent(oldPopup,null) || PopupCallbackGate.IsCurrent(null,null)) throw new Exception("Disposed callback accepted");
            if(!PopupCallbackGate.IsCurrent(newPopup,current)) throw new Exception("Current input rejected");
            if(InputDismissHook.InputCategory(false,0)!="input-keyboard-unmarked" || InputDismissHook.InputCategory(false,32)!="input-keyboard-unmarked") throw new Exception("Keyboard classification");
            if(InputDismissHook.InputCategory(false,16)!="input-keyboard-injected" || InputDismissHook.InputCategory(false,2)!="input-keyboard-injected") throw new Exception("Injected keyboard classification");
            if(InputDismissHook.InputCategory(true,0)!="input-mouse-unmarked" || InputDismissHook.InputCategory(true,1)!="input-mouse-injected" || InputDismissHook.InputCategory(true,2)!="input-mouse-injected") throw new Exception("Mouse classification");
            Rectangle normal=ReminderPopup.CalculateBounds(new Rectangle(0,0,1536,920),new Size(360,100));
            if(normal.X!=1164 || normal.Y!=22 || normal.Width!=360) throw new Exception("Initial top-right bounds");
            Rectangle secondary=new Rectangle(-1920,-120,1920,1080);
            if(!secondary.Contains(ReminderPopup.CalculateBounds(secondary,new Size(400,140)))) throw new Exception("Negative monitor bounds");
            Rectangle tiny=new Rectangle(12,20,15,10);
            if(!tiny.Contains(ReminderPopup.CalculateBounds(tiny,new Size(600,400)))) throw new Exception("Small work area clamp");
            if(CardMonitor.DelayAfterScan(0)!=1000 || CardMonitor.DelayAfterScan(600)!=400 || CardMonitor.DelayAfterScan(1500)!=250) throw new Exception("Scan start cadence and minimum rest");
            using(var form=new ReminderPopup(ReminderSettings.Defaults())) {
                if(form.Visible || form.Location==Point.Empty) throw new Exception("Popup not positioned while hidden");
            }
            Console.WriteLine("Popup lifetime and placement tests passed: 11 groups."); return 0;
        } catch(Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
