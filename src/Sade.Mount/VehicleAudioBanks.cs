using System.Globalization;

namespace Sade.Mount;

/// <summary>Factual vehicle ID to original bank/pitch assignments; IDs400–611 from GTA SA.</summary>
internal static class VehicleAudioBanks
{
    // Factual bank identifiers and pitch values transcribed from the original game's table,
    // documented at https://github.com/gta-reversed/gta-reversed/blob/b53415bde650ed9af94212f77e8d8d842696ecb2/source/game_sa/Audio/Entities/AEVehicleAudioEntity.VehicleAudioSettings.h
    // No upstream implementation is copied or linked. These facts are checked against installed
    // vehicles.ide model numbers and actual sound exports by Sade.Vehicle.Tests --installed.
    // Ordered by installed vehicles.ide model number, not by the asset catalog ordering.
    private static readonly string[] rows =
    [
        "PATHFINDER_P,PATHFINDER_D,1.0", // 400
        "90S_P,90S_D,1.0", // 401
        "COBRA_P,COBRA_D,1.0", // 402
        "MACKTRUCK_P,MACKTRUCK_D,1.0", // 403
        "PACARD_P,PACARD_D,1.0", // 404
        "MERC_P,MERC_D,1.0", // 405
        "MACKTRUCK_P,MACKTRUCK_D,0.84089643", // 406
        "MACKTRUCK_P,MACKTRUCK_D,1.0", // 407
        "JALOPY_P,JALOPY_D,1.0", // 408
        "MERC_P,MERC_D,1.0", // 409
        "PACARD_P,PACARD_D,1.0", // 410
        "COBRA_P,COBRA_D,1.0", // 411
        "CUTLASS_P,CUTLASS_D,1.0", // 412
        "VAN_P,VAN_D,1.0", // 413
        "VAN_P,VAN_D,1.3348398", // 414
        "PORSCHE_P,PORSCHE_D,1.0", // 415
        "VAN_P,VAN_D,1.0", // 416
        "APACHE,,1.0", // 417
        "PACARD_P,PACARD_D,1.0", // 418
        "MUSTANG_P,MUSTANG_D,1.0", // 419
        "MERC_P,MERC_D,1.0", // 420
        "PACARD_P,PACARD_D,1.0", // 421
        "PACARD_P,PACARD_D,1.0", // 422
        "ICEVAN_P,VW_VAN_D,1.0", // 423
        "HOTROD_P,HOTROD_D,1.0", // 424
        "APACHE,,1.0", // 425
        "MERC_P,MERC_D,1.0", // 426
        "JALOPY_P,JALOPY_D,1.3348398", // 427
        "VAN_P,VAN_D,1.0", // 428
        "PORSCHE_P,PORSCHE_D,1.0", // 429
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 430
        "BUS_P,BUS_D,1.0", // 431
        "MACKTRUCK_P,MACKTRUCK_D,1.0", // 432
        "JALOPY_P,JALOPY_D,1.0", // 433
        "HOTROD_P,HOTROD_D,1.0", // 434
        ",,1.0", // 435
        "90S_P,90S_D,1.0", // 436
        "BUS_P,BUS_D,1.0", // 437
        "BUICK_P,BUICK_D,1.0", // 438
        "CUTLASS_P,CUTLASS_D,1.0", // 439
        "VW_VAN_P,VW_VAN_D,1.0", // 440
        "CHAINSAW_P,SCOOTER_D,1.0", // 441
        "PACARD_P,PACARD_D,1.0", // 442
        "MACKTRUCK_P,MACKTRUCK_D,1.0", // 443
        "HEAVY_V8_P,HEAVY_V8_D,1.0", // 444
        "MERC_P,MERC_D,1.0", // 445
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 446
        "R22,,1.0", // 447
        "SCOOTER_P,SCOOTER_D,1.0", // 448
        "TRAIN_P,TRAM_D,1.0", // 449
        ",,1.0", // 450
        "PORSCHE_P,PORSCHE_D,1.0", // 451
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 452
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 453
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 454
        "JALOPY_P,JALOPY_D,1.0", // 455
        "VW_VAN_P,VW_VAN_D,1.0", // 456
        "GOLF_P,GOLF_D,1.0", // 457
        "90S_P,90S_D,1.0", // 458
        "VAN_P,VAN_D,1.0", // 459
        "SINGLEPROP,FASTPROP_D,1.0", // 460
        "SPORTSBIKE_P,SPORTSBIKE_D,1.1892071", // 461
        "SCOOTER_P,SCOOTER_D,1.0", // 462
        "VTWINBIKE_P,VTWINBIKE_D,1.0", // 463
        "RC_CAR_P,RC_CAR_D,1.0", // 464
        "RC_HELI_P,RC_HELI_D,1.0", // 465
        "BELAIR_P,BELAIR_D,1.0", // 466
        "BELAIR_P,BELAIR_D,1.0", // 467
        "DIRTBIKE_P,DIRTBIKE_D,1.0", // 468
        "R22,,1.0", // 469
        "PATHFINDER_P,PATHFINDER_D,1.1892071", // 470
        "DIRTBIKE_P,DIRTBIKE_D,1.0", // 471
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 472
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 473
        "CADILLAC_P,CADILLAC_D,1.0", // 474
        "CUTLASS_P,CUTLASS_D,1.0", // 475
        "FASTPROP,FASTPROP_D,1.0", // 476
        "90S_P,90S_D,1.0", // 477
        "MODEL_T_P,MODEL_T_D,1.0", // 478
        "PACARD_P,PACARD_D,1.0", // 479
        "PORSCHE_P,PORSCHE_D,1.0", // 480
        "BICYCLE_P,BICYCLE_D,1.0", // 481
        "VAN_P,VAN_D,1.0", // 482
        "VW_VAN_P,VW_VAN_D,1.0", // 483
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 484
        "AIRPORT_P,AIRPORT_D,1.0", // 485
        "MODEL_T_P,MODEL_T_D,1.0", // 486
        "MAVERICK,,1.0", // 487
        "MAVERICK,,1.0", // 488
        "PATHFINDER_P,PATHFINDER_D,1.0", // 489
        "PATHFINDER_P,PATHFINDER_D,1.0", // 490
        "PACARD_P,PACARD_D,1.0", // 491
        "PACARD_P,PACARD_D,1.0", // 492
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 493
        "COBRA_P,COBRA_D,1.0", // 494
        "PATHFINDER_P,PATHFINDER_D,1.1892071", // 495
        "MUSTANG_P,MUSTANG_D,1.0", // 496
        "MAVERICK,,1.0", // 497
        "VW_VAN_P,VW_VAN_D,1.0", // 498
        "MACKTRUCK_P,MACKTRUCK_D,1.3348398", // 499
        "90S_P,90S_D,1.0", // 500
        "RC_HELI_P,RC_HELI_D,1.0", // 501
        "COBRA_P,COBRA_D,1.0", // 502
        "PONTIAC_P,PONTIAC_D,1.0", // 503
        "HEAVY_V8_P,HEAVY_V8_D,1.0", // 504
        "PATHFINDER_P,PATHFINDER_D,1.0", // 505
        "PORSCHE_P,PORSCHE_D,1.0", // 506
        "MERC_P,MERC_D,1.0", // 507
        "VAN_P,VAN_D,1.0", // 508
        "BICYCLE_P,BICYCLE_D,1.0", // 509
        "BICYCLE_P,BICYCLE_D,1.0", // 510
        "TWINPROP,TWINPROP_D,1.0", // 511
        "SINGLEPROP,FASTPROP_D,1.0", // 512
        "FASTPROP,FASTPROP_D,1.0", // 513
        "MACKTRUCK_P,MACKTRUCK_D,1.0", // 514
        "MACKTRUCK_P,MACKTRUCK_D,1.0", // 515
        "90S_P,90S_D,1.0", // 516
        "MUSTANG_P,MUSTANG_D,1.0", // 517
        "CUTLASS_P,CUTLASS_D,1.0", // 518
        ",,1.0", // 519
        ",,1.0", // 520
        "RS427_P,RS427_D,1.0594631", // 521
        "SPORTSBIKE_P,SPORTSBIKE_D,1.0", // 522
        "COPBIKE_P,COPBIKE_D,1.0", // 523
        "MACKTRUCK_P,MACKTRUCK_D,1.0", // 524
        "VAN_P,VAN_D,1.0", // 525
        "BUICK_P,BUICK_D,1.0", // 526
        "90S_P,90S_D,1.0", // 527
        "JALOPY_P,JALOPY_D,1.1892071", // 528
        "BUICK_P,BUICK_D,1.0", // 529
        "FORKLIFT_P,FORKLIFT_D,1.0", // 530
        "MODEL_T_P,MODEL_T_D,1.0", // 531
        "HARVESTER_P,HARVESTER_D,1.0", // 532
        "MUSTANG_P,MUSTANG_D,1.0", // 533
        "CADILLAC_P,CADILLAC_D,1.0", // 534
        "HOTROD_P,HOTROD_D,1.0", // 535
        "CUTLASS_P,CUTLASS_D,1.0", // 536
        "TRAIN_P,TRAIN_D,1.0", // 537
        "TRAIN_P,TRAIN_D,1.0", // 538
        "HOVERCRAFT_P,HOVERCRAFT_D,1.0", // 539
        "BUICK_P,BUICK_D,1.0", // 540
        "BELAIR_P,BELAIR_D,1.1892071", // 541
        "CUTLASS_P,CUTLASS_D,1.0", // 542
        "PACARD_P,PACARD_D,1.0", // 543
        "MACKTRUCK_P,MACKTRUCK_D,1.0", // 544
        "HOTROD_P,HOTROD_D,1.0", // 545
        "BUICK_P,BUICK_D,1.0", // 546
        "90S_P,90S_D,1.0", // 547
        "APACHE,,1.0", // 548
        "PACARD_P,PACARD_D,1.0", // 549
        "BUICK_P,BUICK_D,1.0", // 550
        "90S_P,90S_D,1.0", // 551
        "VAN_P,VAN_D,1.0", // 552
        "TWINPROP,TWINPROP_D,1.0", // 553
        "BUICK_P,BUICK_D,1.0", // 554
        "PONTIAC_P,PONTIAC_D,1.0", // 555
        "HEAVY_V8_P,HEAVY_V8_D,1.1892071", // 556
        "HEAVY_V8_P,HEAVY_V8_D,1.1892071", // 557
        "90S_P,90S_D,1.0", // 558
        "MUSTANG_P,MUSTANG_D,1.0", // 559
        "MERC_P,MERC_D,1.0", // 560
        "MERC_P,MERC_D,1.0", // 561
        "90S_P,90S_D,1.0", // 562
        "APACHE,,1.0", // 563
        "RC_TANK_P,RC_TANK_D,1.0", // 564
        "MUSTANG_P,MUSTANG_D,1.0", // 565
        "MUSTANG_P,MUSTANG_D,1.0", // 566
        "CUTLASS_P,CUTLASS_D,1.0", // 567
        "RS427_P,RS427_D,1.0", // 568
        "TRAIN_P,TRAIN_D,1.0", // 569
        "TRAIN_P,TRAIN_D,1.0", // 570
        "GOKART_P,GOKART_D,1.0", // 571
        "GOKART_P,GOKART_D,1.0", // 572
        "JALOPY_P,JALOPY_D,1.1892071", // 573
        "SWEEPER_P,SWEEPER_D,1.0", // 574
        "CADILLAC_P,CADILLAC_D,1.0", // 575
        "BELAIR_P,BELAIR_D,1.0", // 576
        ",,1.0", // 577
        "BUS_P,BUS_D,1.0", // 578
        "PATHFINDER_P,PATHFINDER_D,1.0", // 579
        "MERC_P,MERC_D,1.0", // 580
        "SPORTSBIKE_P,SPORTSBIKE_D,1.0", // 581
        "VAN_P,VAN_D,1.0", // 582
        "AIRPORT_P,AIRPORT_D,1.0", // 583
        ",,1.0", // 584
        "MERC_P,MERC_D,1.0", // 585
        "COPBIKE_P,COPBIKE_D,0.8908987", // 586
        "90S_P,90S_D,1.0", // 587
        "VW_VAN_P,VW_VAN_D,1.0", // 588
        "MUSTANG_P,MUSTANG_D,1.0", // 589
        "TRAIN_P,TRAIN_D,1.0", // 590
        ",,1.0", // 591
        ",,1.0", // 592
        "SINGLEPROP,FASTPROP_D,1.0", // 593
        ",,1.0", // 594
        "BOAT_INBOARD_P,BOAT_INBOARD_D,1.0", // 595
        "MERC_P,MERC_D,1.0", // 596
        "MERC_P,MERC_D,1.0", // 597
        "MERC_P,MERC_D,1.0", // 598
        "PATHFINDER_P,PATHFINDER_D,1.0", // 599
        "BUICK_P,BUICK_D,1.0", // 600
        "JALOPY_P,JALOPY_D,1.0", // 601
        "COBRA_P,COBRA_D,1.0", // 602
        "PONTIAC_P,PONTIAC_D,1.0", // 603
        "BELAIR_P,BELAIR_D,1.0", // 604
        "PACARD_P,PACARD_D,1.0", // 605
        "TEMP_P,TEMP_D,1.0", // 606
        "TEMP_P,TEMP_D,1.0", // 607
        "TEMP_P,TEMP_D,1.0", // 608
        "VAN_P,VAN_D,1.0", // 609
        "TEMP_P,TEMP_D,1.0", // 610
        ",,1.0", // 611
    ];
    internal static bool TryGet(int modelNumber, out string player, out string dummy, out float pitch)
    {
        player = dummy = ""; pitch = 1;
        var index = modelNumber - 400;
        if ((uint)index >= rows.Length) return false;
        var fields = rows[index].Split(',');
        player = fields[0]; dummy = fields[1];
        pitch = float.Parse(fields[2], CultureInfo.InvariantCulture);
        return true;
    }
}
