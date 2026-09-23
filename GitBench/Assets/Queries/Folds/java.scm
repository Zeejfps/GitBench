; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(block) @fold
(class_body) @fold
(interface_body) @fold
(enum_body) @fold
(constructor_body) @fold
(annotation_type_body) @fold
(switch_block) @fold
(switch_block_statement_group) @fold

(array_initializer) @fold
(element_value_array_initializer) @fold
(argument_list) @fold
(formal_parameters) @fold

(block_comment) @fold
