# What's new in Logzilla

## 3.0.0

### New
- **Ask about your log**: a new **Ask** tab where you ask questions in plain words and get answers drawn from the log itself
- Works with **GitHub Copilot**, **Claude** or **OpenAI**, and you pick the model for each from a list
- **Sign in with GitHub** to use your Copilot subscription: approve a short code in your browser, no password needed
- See how many tokens each answer used, and the running total for the conversation
- **••• › Usage in this conversation** shows tokens sent and received, calls, Copilot premium requests and how full the model's context is
- **AI settings** walks you through setting up GitHub Copilot step by step, and shows which account it uses
- Every answer points to the lines it relies on; click a line number to jump straight to it
- When the assistant searches the log, click **Apply** to turn its search into your own filters
- Right-click any entry and choose **Ask AI about this entry**
- Choose whether the assistant looks at the whole log or only what your filters show
- Open the tab with **Ctrl+8** or **View › Ask about this log**
- Connect providers and choose privacy options in **Tools › AI settings**

### Privacy and security
- The assistant is off until you turn it on, and nothing is sent until you ask a question
- Only the parts of the log needed to answer are sent, never the whole file
- Email and IP addresses, IDs, passwords, keys and tokens are hidden before anything is sent, and you still see the real values
- **What was sent** shows exactly what left your computer, and you can keep a record of it on disk
- The assistant can only read the open log; it cannot change files, run anything or browse the web
- Instructions hidden inside log lines are ignored
- API keys are encrypted for your Windows account and never saved in setup files
- Organisations can turn the assistant off, limit which providers are allowed, require personal data to be hidden, require a record of what was sent, or point it at their own endpoint

### Also new
- **Search hits on the overview strip**: matches show as marks down its right edge, next to the errors, so a few hits in a long log are easy to spot and click to
- **Follow thread**: right-click an entry and choose **Follow thread** to see only what that thread logged, in order
- **Click to copy a field**: click a value such as a CorrelationId in an expanded entry to copy it
- Right-click any word or value for **Copy**, as well as Find, Hide and Highlight
- Expanded entries show up to twelve fields instead of six, with IDs and the thread first

### Improved
- Searching merged logs is dramatically faster: seconds or minutes become a fraction of a second
- Searching JSON logs matches what the entries say, not their field names, so a search no longer returns every line
- The installer is about a third smaller, so it starts sooner
- The **Fields** and **Patterns** tabs no longer pause the window while they fill in, even while following a live log
- Answers in the **Ask** tab stay smooth in long conversations
- Long XML and other large messages show in full in an expanded entry; scroll with the mouse wheel to read to the end

### Fixed
- Opening several files at once no longer leaves some of them only partly loaded
- Logs saved in older Windows encodings show accented characters correctly
- Double-clicking or right-clicking a word now picks the word you clicked, including in expanded entries and JSON logs
- Typing in the **Ask** box no longer triggers keyboard shortcuts
- The scroll bar no longer disappears with **Wrap** on, and the last results can always be reached
- The horizontal scroll bar no longer disappears after a search, leaving text cut off
- The **Ask** tab is no longer hidden when the window is narrow; the tabs wrap onto a second row
- Context lines after a stack trace now start after the whole entry
- A regular expression that takes too long stops with a message instead of leaving the search stuck
- A slow or broken colour rule can no longer freeze the window

## 2.1.1

### New
- Show times in **Log time**, **Local time** or **UTC** with the new button next to **Date**
- Log time is exactly what the file wrote, and stays the default
- Right-click the button, or use **View › Show times**, to pick a clock directly
- The log, timeline, side panels and **TIME** box all switch together
- Logs that record their own time zone convert automatically, including daylight saving
- For logs that don't record a time zone, Logzilla asks once which zone they're in and remembers your answer
- A likely answer is marked **suggested** when Logzilla can tell from the file
- Times that can't be converted yet are dimmed, so they're never mistaken for converted ones
- Change a file's time zone any time from **View › Time zone of this file** or the **Sources** tab

### Fixed
- Merged logs from different time zones are now in the right order
- Setting a file's time zone re-sorts any merged view it's in, and marks stay on their entries
- Line numbers no longer appear to reset to 000000 after a million lines

## 2.1.0

### New
- **Field filters**: show only entries where a field has a certain value, such as a CorrelationId, Feature or status code
- Choose **is**, **contains** or **is not**
- Pick several values for one field to match any of them
- Add filters on different fields to narrow further
- Works on JSON logs and on `key=value` text in plain logs
- Open the filter editor with **+ Field filter**, **Ctrl+Shift+F** or **Edit › Filter by field**
- The editor lists the fields and values your log actually contains, most common first
- Active filters show as chips: click to edit, × to remove, right-click to flip is / is not
- Right-click any entry for **Show only where…** or **Hide where…**
- Click a value in the **Fields** tab to filter to it, or Ctrl+click to hide it
- Filtered values are highlighted in the log
- Field filters are saved and loaded with setup files

### Improved
- Files open up to twice as fast
- Searching is two to five times faster on large files
- Logzilla starts faster and uses less memory
- The Recent files menu no longer freezes when a network drive is offline
- Clicking a value in the **Fields** tab now adds a field filter instead of typing into the search box
- The expanded row is simpler: just **Copy** and **Next ERROR**

### Fixed
- Logs saved in Unicode (UTF-16) format now display correctly

## 2.0.1

### New
- Select several entries by dragging, Shift+clicking, or holding Shift with the arrow keys
- **Ctrl+A** selects every entry shown
- **Ctrl+C** copies the whole selection, one entry per line
- **M** marks or unmarks the whole selection
- **Copy marked entries** is now on the **Edit** menu
- The time column shows the full date and time; the **Date** button hides it to give messages more room
- New **Merge** button in the command bar
- Horizontal scrollbar for wide lines; **Shift+mouse wheel** scrolls sideways
- **Ctrl+Tab** switches between open files
- **Ctrl+1** to **Ctrl+7** switch between side panel tabs
- Works with screen readers and Windows high-contrast themes
- Single installer that needs nothing else installed and no administrator rights

### Fixed
- Searching plain-text logs now finds words anywhere in an entry, including stack traces
- Copying or expanding an entry now shows the full exception instead of cutting it off
- Copying a JSON entry now copies the readable message, not the raw JSON
- XML inside log messages is laid out neatly instead of on one line
- The **Wrap** button now wraps long messages
- The horizontal scrollbar now appears for slightly-too-wide lines
- Double-clicking the timeline clears the time range again
- The time range box can be edited with the keyboard
- All seven side panel tabs are visible, with none hidden behind a menu
- Long values in the side panel wrap instead of being cut off
- Uninstalling no longer shows a false error
- Dialogs show Logzilla's icon
- File sizes display correctly on computers that use a comma as the decimal point

## 1.0.0

### New
- Opens multi-gigabyte logs quickly
- Recognises common timestamp formats automatically
- Reads JSON logs from Serilog, pino, bunyan and Elastic as readable messages
- Merges up to 12 logs onto one timeline
- Search with words, exact phrases, exclusions and regular expressions
- Filter to matches, or highlight them with surrounding context
- Side panels for summary, sources, fields, patterns, entry detail, marks and colours
- Timeline you can drag across to focus on a time range
- Overview strip showing where the errors are
- Follow a log live as it's being written
- Installer that registers `.log` files, with an uninstaller
