from pydantic import BaseModel
from typing import List, Optional


class SearchRequest(BaseModel):
    query: str
    top_k: int = 4
    threshold: float = 0.3


class SearchResult(BaseModel):
    id: int
    topic: str
    content: str
    score: float


class SidecarSearchResponse(BaseModel):
    results: List[SearchResult] = []


class IndexRequest(BaseModel):
    id: int
    topic: str
    content: str


class HealthResponse(BaseModel):
    status: str
    indexed_count: int
    index_loaded_from_disk: Optional[bool] = None
