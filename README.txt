ZPN - Archive to Call Buffer V4.3.19
============================

GUI settings:
- Source folder
- Destination folder
- Temp folder
- Log folder
- Parallel TAR workers
- Search source subfolders (recursive)
- Configurable low-space reserve
- Normal or Debug High logging
- Password-protected TAR deletion option

Standalone WAV/XML support:
- The processor scans for normal WAV/XML files directly in the source folder.
- When "Search source subfolders (recursive)" is checked, it also searches all source subfolders.
- When unchecked, standalone files and TAR files are searched only at the source root.
- A standalone WAV must have an exact 15-digit basename.
- Its matching XML must have the same exact 15-digit basename and be in the same source directory.
- Standalone source WAV/XML files are COPIED to the organized destination; the originals are not moved or deleted.
- Destination duplicates are never overwritten.
- Destination, Temp, and Log trees are excluded from standalone scanning if they happen to be located beneath Source.
- Standalone actions are written to the CSV log using [STANDALONE] and statuses such as STANDALONE_COPIED, STANDALONE_MISSING_XML, STANDALONE_DUPLICATE, and STANDALONE_INVALID.

TAR deletion:
- Locked by default.
- The passcode is not stored in clear text.
- The GUI uses a salted PBKDF2-SHA256 verifier.
- Processing is native C#; no PowerShell process or script is launched.
- TAR deletion is only allowed after the GUI has successfully unlocked it and only for a fully successful TAR under the processor's existing safety rules.

Terminal output is colorized based on action, including scanning, standalone copies, extraction, moves, warnings, errors, disk-space states, and completion.

UI SCALING UPDATE
-----------------
The main window now uses Font autoscaling and responsive TableLayoutPanel/FlowLayoutPanel sizing.
Path labels and Browse buttons use AutoSize columns, while path text boxes consume the remaining width.
Start/Stop controls and the TAR deletion unlock dialog also auto-size with the selected font.
This prevents clipped controls when changing the MainForm font or using Windows display scaling.


FRESH LOG OPTION
----------------
The GUI includes "Clear old processor logs before starting (fresh run)".
When enabled, Audiolog_*.csv files and legacy Audio_*.csv files in the selected Log folder are deleted before the new run begins.
Other files in the Log folder are not deleted. Clearing these processor logs also resets previous-log resume history, so TARs are evaluated again as a fresh run.
The option is OFF by default.
