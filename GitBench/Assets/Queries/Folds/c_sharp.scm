; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(block) @fold
(declaration_list) @fold
(accessor_list) @fold
(enum_member_declaration_list) @fold
(switch_body) @fold
(switch_section) @fold
(switch_expression) @fold

(initializer_expression) @fold
(anonymous_object_creation_expression) @fold
(collection_expression) @fold
(argument_list) @fold
(parameter_list) @fold

(raw_string_literal) @fold
(verbatim_string_literal) @fold
(interpolated_string_expression) @fold
(comment) @fold

(preproc_if) @fold
