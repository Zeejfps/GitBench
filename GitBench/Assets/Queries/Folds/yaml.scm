; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(block_mapping_pair) @fold
(block_sequence_item) @fold
(flow_mapping) @fold
(flow_sequence) @fold
