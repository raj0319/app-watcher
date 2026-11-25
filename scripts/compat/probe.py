"""Print the controls of a program's main window (starts its own instance, kills it after). Used to write a drive."""
import subprocess, sys, time
from pywinauto import Desktop

exe = sys.argv[1]; wait = float(sys.argv[2]) if len(sys.argv) > 2 else 4
p = subprocess.Popen([exe] + sys.argv[3:])
time.sleep(wait)
try:
    wins = Desktop(backend="uia").windows(process=p.pid)
    for w in wins:
        print("WINDOW", repr(w.window_text()), w.element_info.control_type, w.element_info.class_name)
        for d in w.descendants()[:250]:
            ei = d.element_info
            if ei.control_type in ("Pane", "Group", "Custom", "Image", "Separator", "Thumb", "ScrollBar") and not ei.name: continue
            print("  ", ei.control_type, repr(ei.name)[:50], repr(ei.automation_id)[:30])
finally:
    p.kill()
