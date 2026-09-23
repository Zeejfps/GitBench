; What folds besides a declaration: every construct that brackets lines of its own. Only a
; region spanning three lines or more survives — the opening and closing lines stay on screen,
; so anything shorter has nothing between them to hide — and where several start on one line
; the widest wins.

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

(arguments) @fold
(formal_parameters) @fold
(type_arguments) @fold
(parenthesized_expression) @fold

(named_imports) @fold
(export_clause) @fold

(jsx_element) @fold
(jsx_self_closing_element) @fold

(template_string) @fold
(comment) @fold
