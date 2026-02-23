# TimeMachine.Agent

TimeMachine.Agent is a lightweight Windows activity tracker written in C# (.NET).

The application monitors active windows, detects idle time, stores sessions in SQLite,
and generates productivity analytics directly in the console.

---

## 🚀 Features

- Active window tracking (app name, PID, title, exe path)
- Idle time detection (configurable threshold)
- SQLite session storage
- GitHub public events integration (via API)
- Real-time session merging with grace period
- Console productivity reports:

| Key | Report |
|-----|--------|
| S   | General overview (active / idle / top apps) |
| F   | Focus analysis (deep work sessions + focus score) |
| P   | Pattern analysis (switch behavior + peak hour) |
| D   | Distractions (short sessions + switch targets) |
| G   | GitHub activity (today events) |

---

## 🧠 Focus Analytics Logic

- Deep work threshold (default: 20 min)
- Short distraction threshold (default: 90 sec)
- App switching penalty
- Focus score calculation (0–100%)

---

## 🛠 Tech Stack

- C# (.NET 8)
- Microsoft.Data.Sqlite
- WinAPI (user32.dll)
- GitHub REST API
- Console-based architecture

---

## 📦 Database Structure

### `sessions`
Stores application sessions:

- start_ts
- end_ts
- duration_ms
- app
- pid
- window_title
- exe_path
- is_idle

### `github_events`
Stores pulled GitHub public activity:

- gh_event_id
- gh_type
- repo
- created_ts
- payload_json

---

## ⚙ How to Run

1. Clone repository
2. Open in Visual Studio
3. Run project
4. (Optional) Set GitHub token:
