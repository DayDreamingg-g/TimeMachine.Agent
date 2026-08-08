# ⏱️ TimeMachine.Agent

> A lightweight Windows productivity tracker built with **C# / .NET 8**, **SQLite**, and **WinAPI**.

TimeMachine.Agent monitors active applications, detects idle time, stores activity sessions locally, and analyzes focus and distraction patterns.

The project was built to explore Windows system APIs, local data persistence, REST API integration, and productivity analytics.

---

## 🚀 Features

- 🖥️ Tracks the currently active Windows application
- ⌨️ Detects user idle time through WinAPI
- 💾 Stores activity sessions locally in SQLite
- 🧠 Detects deep-focus sessions and distractions
- 🔄 Merges short application switches using a grace period
- 📊 Generates productivity reports directly in the console
- 🐙 Fetches public GitHub activity through the GitHub REST API

### Available Reports

| Key | Report |
| --- | --- |
| `S` | General activity overview — active/idle time and top applications |
| `F` | Focus analysis — deep-work sessions and focus score |
| `P` | Pattern analysis — switching behavior and peak productivity |
| `D` | Distraction analysis — short sessions and frequent switches |
| `G` | GitHub activity — today's public events |

---

## 🧠 Focus Analytics

TimeMachine.Agent uses configurable thresholds to classify activity:

- **Deep work:** sessions longer than 20 minutes
- **Short distraction:** sessions shorter than 90 seconds
- **Idle detection:** configurable timeout
- **Grace period:** prevents brief app switches from unnecessarily splitting sessions
- **Focus score:** calculated from recorded activity patterns

This allows raw window activity to be transformed into higher-level productivity metrics.

---

## 🛠️ Tech Stack

- **C#**
- **.NET 8**
- **Microsoft.Data.Sqlite**
- **WinAPI (`user32.dll`)**
- **GitHub REST API**
- **SQLite**
- **Git / GitHub**

---

## 🗄️ Data Storage

Activity is stored locally in SQLite.

### `sessions`

Stores application activity:

- `start_ts`
- `end_ts`
- `duration_ms`
- `app`
- `pid`
- `window_title`
- `exe_path`
- `is_idle`

### `github_events`

Stores retrieved GitHub activity:

- `gh_event_id`
- `gh_type`
- `repo`
- `created_ts`
- `payload_json`

---

## ▶️ Getting Started

### Requirements

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 or another .NET-compatible IDE

### Run locally

1. Clone the repository:

```bash
git clone https://github.com/DayDreamingg-g/TimeMachine.Agent.git
```

2. Open `TimeMachine.Agent.slnx`.
3. Restore dependencies and build the project.
4. Run `TimeMachine.Agent`.

GitHub integration is optional and may require a GitHub token depending on API usage.

---

## 🎯 What I Learned

This project gave me practical experience with:

- interacting with native Windows APIs from C#
- designing persistent local storage with SQLite
- processing time-series activity data
- consuming REST APIs
- implementing configurable analytics logic
- structuring a standalone .NET application

---

## 📌 Project Status

The core tracking and analytics functionality is implemented.

Future improvements may include:

- graphical desktop interface
- visual productivity charts
- configuration UI
- exportable reports
- automated tests

---

## 👤 Author

**Oleksandr — DayDreamingg-g**

Computer Science student focused on software development and cybersecurity.
