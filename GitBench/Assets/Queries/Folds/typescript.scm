; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(statement_block) @fold
(class_body) @fold
(switch_body) @fold

(object) @fold
(array) @fold
(object_pattern) @fold
(array_pattern) @fold
(object_type) @fold
(enum_body) @fold
(interface_body) @fold
(type_arguments) @fold

(arguments) @fold
(formal_parameters) @fold
(parenthesized_expression) @fold

(named_imports) @fold
(export_clause) @fold

(template_string) @fold
(comment) @fold
