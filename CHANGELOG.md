# What's new in Logzilla

## 3.2.0

### New
- **File › Export shown entries as plain text** turns a JSON log (CLEF, Bunyan, pino, ECS) into an ordinary text log: one `time LEVEL message` line per record, with any stack trace on the lines below it. Times are written in the clock the time column shows. Fields other than the time, level, message and exception are left out

### Fixed
- **Copied entries had no time.** Ctrl+C copied only the message, without the time and level the grid shows in their own columns, so a run of JSON entries pasted elsewhere could not be put in order. Each copied entry now starts with its time and level

## 3.1.1

### Security
- **A log could change your AI settings.** Right-clicking a value in an expanded entry and choosing **Highlight** saved it to the settings file as it was. A value with a line break in it, which anyone writing to the log can plant, put a line of its own into that file, and the next start read it as a real setting. For example, it could point your OpenAI or Claude endpoint at someone else's server, so your API key and the log excerpts you asked about went there. Line breaks can no longer get into the settings file, and an endpoint that is not https is refused even if it does
- **Parts of hidden values could reach the AI.** The assistant's tools shortened long lines (300 characters in lists, 3,000 in whole entries) before hiding personal data. A token or email address cut in two no longer looked like one, so the half before the cut went out unhidden, such as the payload of a sign-in token or the start of an address. Values are now hidden first and shortened after
- **Your API key could be sent unencrypted.** **Tools › AI settings** accepted any endpoint, including `http://` ones, which send your key and log excerpts where anyone on the network can read them. Endpoints must now use `https://`. Plain `http://` is still allowed for `localhost`, where local gateways run. An endpoint set by your organisation's policy is unchanged
- **The AI could keep your computer busy for hours.** The assistant chooses its own searches, and a log can try to steer it. A search pattern that is slow on every line kept a processor core busy until Logzilla closed, with no way to stop it. Each search the assistant runs now stops after 60 seconds and asks it to narrow the search
- Smaller hardening: Sign in with GitHub opens only an `https://` web page, never another kind of address GitHub's reply might contain. Logzilla no longer looks for the Copilot CLI in folders named relative to the current folder, which is often the folder of the log you opened. The uninstaller starts Windows' own `cmd.exe` by its full path

### Fixed
- **Tools › Open settings folder** did nothing. It now opens the folder in Explorer
- **Merging a file that was still loading** quietly left out every line after the point loading had reached, and miscounted its lines. Merge now asks you to wait until loading finishes
- **A regular expression that is not valid** (a half-typed `(`, say) searched for nothing and showed every entry under a red "no hits", as if everything had matched. The hit count now says **invalid regex** and the status bar explains why
- **Messages in the status bar** were cut off under the line counts on the right, among them the message for a search that took too long. They now stop before the counts with "…" and show in full when you point at them
- **The hit count** wrapped onto a hidden second line when it was long ("1,234,567 hits" showed as "1,234,567")
- **On a window narrower than about 1,000 pixels**, the Merge, Wrap and other buttons were drawn on top of the **Highlight / Filter to matches** switch. Merge, which is also **File › Merge** (Ctrl+M), now steps aside when there is no room, and the switch stays clear
- **Live tail after a log rotation:** when a followed file was emptied and started again, a selected line pointed at a different line than before, and some rows dimmed the wrong part of their text. Both are now reset along with the file
- **Load setup** stopped partway through on a value it could not read, after it had already cleared your highlights and field filters. It now skips that one line and loads the rest

### Faster
- **Live tail no longer freezes the window.** New lines were read on the window's own thread. A burst of output, or a rotated file being read again from the start, froze Logzilla until it finished. New lines are now read in the background, like opening a file
- **Long lines draw faster.** Each row on screen handed its whole message, up to 100,000 characters, to Windows to draw, on every repaint and every mouse movement, although only the part that fits on screen can show. Now only that part is drawn. Logs with large payloads or long JSON lines scroll and respond to the mouse more smoothly
- **Changing a regular-expression or field-filter search stops the previous one sooner.** An outdated search used to run for up to 65,536 more lines before noticing it had been replaced. It now checks every 4,096 lines, so typing a pattern one character at a time no longer leaves earlier searches competing for the processor
- **JSON logs index with fewer memory allocations.** Reading each line's level used to create three temporary objects per line, about 30 million for a 10-million-line log. It now reads the level in place

### Known issues
- On a narrow window, the **Context** stepper and **Time** button (and Merge, see above) are not shown, and nothing else offers them. They were already hidden under other buttons before this change
- The uninstaller always removes the default install folder. A copy installed somewhere else with `/dir=` is left on disk

## 3.1.0

### New
- Add highlights right on the **Highlighting** tab: type a word or phrase at the top and press Enter, or turn on **.\*** for a regular expression
- Right-click any highlight to switch it on or off, change its colour, match it as a regular expression, or remove it

### Changed
- Tabs renamed: **Colors** is now **Highlighting**, **Marks** is now **Bookmarks**, and **Ask** is now **AI Chat**
- Logzilla no longer adds highlights of its own, so nothing is coloured until you choose to. The three it used to add for errors, timeouts and IP addresses are removed; highlights you added yourself are kept

### Improved
- Each tab keeps its own search: switching tabs no longer carries what you typed to the other log, and switching back brings your search, severity chips, time range and field filters back with it
- Changing the filter keeps your place: pick an entry while showing only errors, switch the other severities back on, and that entry stays right where it was on screen with its surrounding lines around it
- Click an expanded entry again to fold it back to one line, or press **Esc**; click once more to open it. This works however quickly you click
- Double-clicking a word no longer adds it to the Find box, because it got in the way of clicking entries open and closed. Right-click the word and choose **Find** instead
- Long messages are always shown in full: no more "..." at the end of a long line, a long exception headline in a JSON log, or a long value in an expanded entry's fields
- With **Wrap** on, very long messages wrap onto as many lines as they need; scroll with the mouse wheel to read to the end

### Fixed
- Highlights no longer disappear when you click a highlighted line open
- Closing a tab to the left of the one you are reading no longer switches you to a different log

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
