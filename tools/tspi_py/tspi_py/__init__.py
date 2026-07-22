"""Memory-mapped reader for .tspi trajectory files."""

from .dcv import DcvFlyout, dcv_flyouts
from .engagements import Engagement, engagements, save_mat
from .reader import TspiFile, Entity, Event, RECORD_DTYPE

__all__ = ["TspiFile", "Entity", "Event", "RECORD_DTYPE",
           "Engagement", "engagements", "save_mat",
           "DcvFlyout", "dcv_flyouts"]
__version__ = "0.1.0"
