; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(class_body) @fold
(enum_class_body) @fold
(control_structure_body) @fold
(lambda_literal) @fold
(when_expression) @fold
(when_entry) @fold
(catch_block) @fold
(finally_block) @fold
(getter) @fold
(setter) @fold
(anonymous_initializer) @fold

(value_arguments) @fold
(function_value_parameters) @fold
(collection_literal) @fold
(import_list) @fold

(multiline_comment) @fold
