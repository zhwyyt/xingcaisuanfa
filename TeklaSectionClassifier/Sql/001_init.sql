CREATE TABLE IF NOT EXISTS members (
  member_id TEXT PRIMARY KEY,
  assembly_id TEXT,
  guid TEXT,
  name TEXT,
  profile_string TEXT,
  material TEXT,
  main_axis_origin_x REAL,
  main_axis_origin_y REAL,
  main_axis_origin_z REAL,
  main_axis_dir_x REAL,
  main_axis_dir_y REAL,
  main_axis_dir_z REAL,
  axis_length REAL,
  bbox_min_x REAL,
  bbox_min_y REAL,
  bbox_min_z REAL,
  bbox_max_x REAL,
  bbox_max_y REAL,
  bbox_max_z REAL,
  extract_time TEXT
);

CREATE TABLE IF NOT EXISTS parts (
  part_id TEXT PRIMARY KEY,
  member_id TEXT NOT NULL,
  guid TEXT,
  name TEXT,
  part_type TEXT,
  profile_string TEXT,
  material TEXT,
  thickness REAL,
  area REAL,
  volume REAL,
  centroid_x REAL,
  centroid_y REAL,
  centroid_z REAL,
  axis_proj_start REAL,
  axis_proj_end REAL,
  axis_proj_length REAL,
  coverage_ratio REAL,
  near_start INTEGER,
  near_end INTEGER,
  is_bent_like INTEGER,
  is_folded_like INTEGER,
  outer_side_candidate INTEGER,
  FOREIGN KEY(member_id) REFERENCES members(member_id)
);

CREATE TABLE IF NOT EXISTS part_connections (
  member_id TEXT NOT NULL,
  part_id_1 TEXT NOT NULL,
  part_id_2 TEXT NOT NULL,
  relation_type TEXT,
  score REAL,
  PRIMARY KEY (member_id, part_id_1, part_id_2)
);

CREATE TABLE IF NOT EXISTS section_samples (
  sample_id TEXT PRIMARY KEY,
  member_id TEXT NOT NULL,
  ratio REAL,
  station REAL,
  is_abnormal INTEGER,
  abnormal_reason TEXT,
  closed_loops INTEGER,
  major_plate_count INTEGER,
  outer_width REAL,
  outer_height REAL,
  cavity_count INTEGER,
  horizontal_symmetry_score REAL,
  vertical_symmetry_score REAL,
  central_vertical_plate_count INTEGER,
  central_horizontal_plate_count INTEGER,
  enclosure_score REAL,
  FOREIGN KEY(member_id) REFERENCES members(member_id)
);

CREATE TABLE IF NOT EXISTS section_sample_parts (
  sample_id TEXT NOT NULL,
  part_id TEXT NOT NULL,
  role_hint TEXT,
  cut_length REAL,
  thickness REAL,
  local_pos_1 REAL,
  local_pos_2 REAL,
  PRIMARY KEY (sample_id, part_id),
  FOREIGN KEY(sample_id) REFERENCES section_samples(sample_id)
);

CREATE TABLE IF NOT EXISTS classification_runs (
  run_id TEXT,
  member_id TEXT,
  main_class TEXT,
  sub_reason_code TEXT,
  labels TEXT,
  key_dims TEXT,
  confidence REAL,
  need_manual_review INTEGER,
  score_h REAL,
  score_box REAL,
  score_t REAL,
  score_cross REAL,
  score_irregular REAL,
  created_at TEXT,
  PRIMARY KEY (run_id, member_id)
);
