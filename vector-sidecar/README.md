# ChatApp Vector Sidecar

A lightweight **Python FastAPI + FAISS** service that provides semantic Knowledge Base search for the ChatApp .NET backend.

## What It Does

- Reads all `KnowledgeBase` table entries from SQL Server at startup
- Embeds them using **Gemini `gemini-embedding-001`** (768-dim, truncated via `outputDimensionality`)
- Stores them in a **FAISS `IndexFlatIP`** (cosine similarity via L2-normalised inner product)
- Exposes 4 HTTP endpoints the .NET `VectorSidecarKBService` calls

## Prerequisites

- Python 3.11+ (3.14 also works)
- **ODBC Driver 17 for SQL Server** (already installed if you've run the .NET app — check with `pyodbc.drivers()`)
- A Gemini API key

## Setup

### 1. Create virtual environment

```bash
cd ChatApp/vector-sidecar
python -m venv venv
venv\Scripts\activate      # Windows
# source venv/bin/activate  # macOS/Linux
```

### 2. Install dependencies

```bash
pip install -r requirements.txt
```

### 3. Configure environment

```bash
copy .env.example .env
# Edit .env and fill in GEMINI_API_KEY
```

The `.env` file:
```
GEMINI_API_KEY=your-key-here
DB_SERVER=(localdb)\mssqllocaldb
DB_DATABASE=ChatAppDb
SIDECAR_PORT=8001
```

### 4. Run

```bash
uvicorn main:app --host 127.0.0.1 --port 8001
```

On first run, the sidecar will:
1. Read all rows from the `KnowledgeBase` SQL table
2. Embed them via Gemini (takes a few seconds depending on KB size)
3. Save `index.faiss` and `id_map.json` to disk

On subsequent runs, it loads from disk instantly.

## Endpoints

| Method | Path | Description |
|:---|:---|:---|
| `GET` | `/health` | Readiness check, returns indexed count |
| `POST` | `/search` | Semantic search — body: `{query, top_k, threshold}` |
| `POST` | `/index` | Add/update a KB entry — body: `{id, topic, content}` |
| `DELETE` | `/index/{id}` | Remove a KB entry from the index |

## Example: Search

```bash
curl -X POST http://localhost:8001/search \
  -H "Content-Type: application/json" \
  -d '{"query": "reset my password", "top_k": 4, "threshold": 0.3}'
```

Response:
```json
{
  "results": [
    {"id": 3, "topic": "Password Reset", "content": "...", "score": 0.87},
    {"id": 7, "topic": "Account Recovery", "content": "...", "score": 0.71}
  ]
}
```

## When the Sidecar is Down

The .NET `VectorSidecarKBService` automatically falls back to keyword search against the SQL database. The AI bot keeps working — users see no error.

## Files

```
vector-sidecar/
├── main.py                  # FastAPI app + lifespan startup
├── models.py                # Pydantic request/response models
├── requirements.txt         # Python dependencies
├── .env.example             # Environment variable template
├── .env                     # Your local config (not committed)
├── index.faiss              # FAISS binary index (auto-generated)
├── id_map.json              # DB ID ↔ FAISS ID mapping (auto-generated)
└── services/
    ├── __init__.py
    ├── embedding.py         # Gemini gemini-embedding-001 (768-dim) + L2 normalisation
    ├── faiss_index.py       # Thread-safe FaissIndex class (IndexFlatIP)
    └── db_loader.py         # SQL Server reader via pyodbc (Windows Auth)
```
