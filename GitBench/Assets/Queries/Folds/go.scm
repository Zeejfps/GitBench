; What folds besides a declaration. Only a construct spanning three lines or more survives, and
; where several start on one line the widest wins. A fold ending on a closing bracket pulls that
; line up behind its chip; one ending on content hides it with the rest.

(block) @fold
(literal_value) @fold
(field_declaration_list) @fold
(interface_type) @fold
(import_spec_list) @fold
(var_declaration) @fold
(const_declaration) @fold

(expression_switch_statement) @fold
(type_switch_statement) @fold
(select_statement) @fold
(expression_case) @fold
(default_case) @fold
(communication_case) @fold

(argument_list) @fold
(parameter_list) @fold

(raw_string_literal) @fold
(comment) @fold
