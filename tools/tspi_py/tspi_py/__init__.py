"""Memory-mapped reader for .tspi trajectory files."""

from .engagements import Engagement, engagements, save_mat
from .reader import TspiFile, Entity, Event, RECORD_DTYPE

__all__ = ["TspiFile", "Entity", "Event", "RECORD_DTYPE",
           "Engagement", "engagements", "save_mat"]
__version__ = "0.1.0"
