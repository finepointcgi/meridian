// Meridian's own declarations of the SysML v2 / KerML standard library names used for name resolution.
// Written for Meridian from the public OMG SysML v2 specification's package and element names; this is not a copy of the
// SysML v2 Pilot Implementation library files. Bodies are reduced to what name resolution needs.

standard library package Base {
    abstract part def Anything;
    abstract attribute def DataValue;
    abstract attribute dataValues : DataValue[0..*];
    abstract part things : Anything[0..*];
    abstract part self : Anything;
    attribute def Natural;
}

standard library package ScalarValues {
    abstract attribute def ScalarValue;
    attribute def Boolean :> ScalarValue;
    attribute def String :> ScalarValue;
    abstract attribute def NumericalValue :> ScalarValue;
    attribute def Number :> NumericalValue;
    attribute def Complex :> NumericalValue;
    attribute def Real :> Complex;
    attribute def Rational :> Real;
    attribute def Integer :> Rational;
    attribute def Natural :> Integer;
    attribute def Positive :> Natural;
    attribute def UnlimitedNatural :> Natural;
}

standard library package Links {
    abstract part def Link;
    abstract part def BinaryLink :> Link;
    abstract part links : Link[0..*];
    abstract part binaryLinks : BinaryLink[0..*];
}

standard library package Occurrences {
    abstract occurrence def Occurrence;
    abstract occurrence occurrences : Occurrence[0..*];
    abstract occurrence def Life :> Occurrence;
    abstract occurrence def Situation :> Occurrence;
    attribute def HappensBefore;
    attribute def HappensDuring;
    abstract occurrence timeSlices : Occurrence[0..*];
    abstract occurrence snapshots : Occurrence[0..*];
}

standard library package Objects {
    abstract occurrence def Object;
    abstract occurrence objects : Object[0..*];
    abstract occurrence def Body :> Object;
    abstract occurrence def Point :> Object;
}

standard library package Items {
    abstract item def Item;
    abstract item items : Item[0..*];
    abstract item subitems : Item[0..*];
    abstract item def Touches;
}

standard library package Parts {
    abstract part def Part :> Items::Item;
    abstract part parts : Part[0..*];
    abstract part subparts : Part[0..*];
    abstract port ownedPorts : Ports::Port[0..*];
}

standard library package Ports {
    abstract port def Port;
    abstract port ports : Port[0..*];
    abstract port subports : Port[0..*];
}

standard library package Connections {
    abstract connection def Connection;
    abstract connection def BinaryConnection :> Connection;
    abstract connection connections : Connection[0..*];
    abstract connection binaryConnections : BinaryConnection[0..*];
}

standard library package Interfaces {
    abstract interface def Interface;
    abstract interface def BinaryInterface :> Interface;
    abstract interface interfaces : Interface[0..*];
    abstract interface binaryInterfaces : BinaryInterface[0..*];
}

standard library package Allocations {
    abstract allocation def Allocation;
    abstract allocation allocations : Allocation[0..*];
    abstract allocation suballocations : Allocation[0..*];
}

standard library package Flows {
    abstract item def Message;
    abstract item def Flow;
    abstract item def SuccessionFlow :> Flow;
    abstract item messages : Message[0..*];
    abstract item flows : Flow[0..*];
}

standard library package Actions {
    abstract action def Action;
    abstract action actions : Action[0..*];
    abstract action subactions : Action[0..*];
    action def AcceptAction :> Action;
    action def SendAction :> Action;
    action def AssignmentAction :> Action;
    action def IfThenAction :> Action;
    action def IfThenElseAction :> IfThenAction;
    action def LoopAction :> Action;
    action def WhileLoopAction :> LoopAction;
    action def ForLoopAction :> LoopAction;
    action def DecisionAction :> Action;
    action def MergeAction :> Action;
    action def ForkAction :> Action;
    action def JoinAction :> Action;
    action def TerminateAction :> Action;
    action def ControlAction :> Action;
    action start : Action;
    action done : Action;
}

standard library package States {
    abstract state def StateAction :> Actions::Action;
    abstract state states : StateAction[0..*];
    abstract state substates : StateAction[0..*];
    abstract action def StateTransitionAction :> Actions::Action;
    abstract action transitions : StateTransitionAction[0..*];
}

standard library package Calculations {
    abstract calc def Calculation;
    abstract calc calculations : Calculation[0..*];
    abstract calc subcalculations : Calculation[0..*];
}

standard library package Constraints {
    abstract constraint def ConstraintCheck;
    abstract constraint constraintChecks : ConstraintCheck[0..*];
    abstract constraint assertedConstraints : ConstraintCheck[0..*];
    abstract constraint negatedConstraints : ConstraintCheck[0..*];
}

standard library package Requirements {
    abstract requirement def RequirementCheck;
    abstract requirement requirementChecks : RequirementCheck[0..*];
    abstract concern def ConcernCheck :> RequirementCheck;
    abstract concern concernChecks : ConcernCheck[0..*];
    abstract requirement def DesignConstraintCheck :> RequirementCheck;
    abstract requirement def FunctionalRequirementCheck :> RequirementCheck;
    abstract requirement def InterfaceRequirementCheck :> RequirementCheck;
    abstract requirement def PerformanceRequirementCheck :> RequirementCheck;
    abstract requirement def PhysicalRequirementCheck :> RequirementCheck;
}

standard library package Cases {
    abstract case def Case;
    abstract case cases : Case[0..*];
    abstract case subcases : Case[0..*];
}

standard library package AnalysisCases {
    abstract analysis def AnalysisCase :> Cases::Case;
    abstract analysis analysisCases : AnalysisCase[0..*];
}

standard library package VerificationCases {
    abstract verification def VerificationCase :> Cases::Case;
    abstract verification verificationCases : VerificationCase[0..*];
    enum def VerdictKind {
        enum pass;
        enum fail;
        enum inconclusive;
        enum error;
    }
    calc def PassIf {
        in isPassing : ScalarValues::Boolean;
        return verdict : VerdictKind;
    }
    metadata def VerificationMethod {
        attribute kind : VerificationMethodKind[1..*];
    }
    enum def VerificationMethodKind {
        enum inspect;
        enum analyze;
        enum demo;
        enum test;
    }
}

standard library package UseCases {
    abstract use case def UseCase :> Cases::Case;
    abstract use case useCases : UseCase[0..*];
}

standard library package Views {
    abstract view def View;
    abstract view views : View[0..*];
    abstract viewpoint def Viewpoint;
    abstract viewpoint viewpoints : Viewpoint[0..*];
    abstract rendering def Rendering;
    abstract rendering renderings : Rendering[0..*];
    rendering def TextualRendering :> Rendering;
    rendering def GraphicalRendering :> Rendering;
    rendering def TabularRendering :> Rendering;
    rendering asTextualNotation : TextualRendering;
    rendering asTreeDiagram : GraphicalRendering;
    rendering asInterconnectionDiagram : GraphicalRendering;
    rendering asElementTable : TabularRendering;
}

standard library package Metadata {
    abstract metadata def MetadataItem;
    abstract metadata metadataItems : MetadataItem[0..*];
    metadata def SemanticMetadata :> MetadataItem;
}

standard library package Collections {
    abstract attribute def Collection;
    attribute def OrderedCollection :> Collection;
    attribute def UniqueCollection :> Collection;
    attribute def Array :> OrderedCollection;
    attribute def Bag :> Collection;
    attribute def Set :> UniqueCollection;
    attribute def OrderedSet :> OrderedCollection, UniqueCollection;
    attribute def List :> OrderedCollection;
    attribute def KeyValuePair;
    attribute def Map :> Collection;
    attribute def OrderedMap :> Map;
}

standard library package BaseFunctions {
    calc def '==';
    calc def '!=';
    calc def ToString;
    calc def size;
    calc def isEmpty;
    calc def notEmpty;
    calc def includes;
    calc def excludes;
}

standard library package NumericalFunctions {
    calc def abs;
    calc def sum;
    calc def product;
    calc def max;
    calc def min;
}

standard library package RealFunctions {
    calc def sqrt;
    calc def floor;
    calc def round;
    calc def ceil;
}

standard library package TrigFunctions {
    attribute pi : ScalarValues::Real;
    calc def sin;
    calc def cos;
    calc def tan;
    calc def arcsin;
    calc def arccos;
    calc def arctan;
}

standard library package ControlFunctions {
    calc def forAll;
    calc def exists;
    calc def collect;
    calc def select;
    calc def reject;
    calc def reduce;
}

standard library package SequenceFunctions {
    calc def size;
    calc def including;
    calc def excluding;
}

standard library package Time {
    attribute def TimeInstantValue :> ScalarValues::Real;
    attribute def DurationValue :> ScalarValues::Real;
    part def Clock;
    part universalClock : Clock;
    attribute def Iso8601DateTime;
}

standard library package SysML {
    metadata def PartUsage;
    metadata def PartDefinition;
    metadata def AttributeUsage;
    metadata def AttributeDefinition;
    metadata def PortUsage;
    metadata def PortDefinition;
    metadata def ItemUsage;
    metadata def ActionUsage;
    metadata def ActionDefinition;
    metadata def StateUsage;
    metadata def RequirementUsage;
    metadata def RequirementDefinition;
    metadata def ConnectionUsage;
    metadata def InterfaceUsage;
    metadata def Usage;
    metadata def Definition;
    metadata def Element;
}

standard library package ModelingMetadata {
    enum def StatusKind {
        enum open;
        enum tbd;
        enum tbr;
        enum tbc;
        enum done;
        enum closed;
    }
    metadata def StatusInfo {
        attribute originator : ScalarValues::String[0..1];
        attribute owner : ScalarValues::String[0..1];
        attribute status : StatusKind;
        attribute risk : RiskMetadata::Risk[0..1];
    }
    metadata def Rationale {
        attribute text : ScalarValues::String;
    }
    metadata def Issue {
        attribute text : ScalarValues::String;
    }
    metadata def Refinement;
}

standard library package RiskMetadata {
    attribute def Level :> ScalarValues::Real;
    enum def LevelEnum {
        enum low;
        enum medium;
        enum high;
    }
    attribute def RiskLevel {
        attribute probability : Level;
        attribute impact : Level[0..1];
    }
    metadata def Risk {
        attribute totalRisk : RiskLevel[0..1];
        attribute technicalRisk : RiskLevel[0..1];
        attribute scheduleRisk : RiskLevel[0..1];
        attribute costRisk : RiskLevel[0..1];
    }
}

standard library package RequirementDerivation {
    metadata def OriginalRequirementMetadata;
    metadata def DerivedRequirementMetadata;
    connection def DerivationConnection;
    metadata def Derivation;
}

standard library package TradeStudies {
    abstract calc def EvaluationFunction;
    abstract requirement def TradeStudyObjective;
    requirement def MinimizeObjective :> TradeStudyObjective;
    requirement def MaximizeObjective :> TradeStudyObjective;
    abstract analysis def TradeStudy;
}

standard library package CauseAndEffect {
    metadata def CausationMetadata;
    metadata def Cause;
    metadata def Effect;
}
